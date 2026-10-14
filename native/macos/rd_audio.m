//
//  rd_audio.m
//
//  System audio behind the C ABI in rd_native.h: ScreenCaptureKit for capture, AudioQueue for playback.
//
//  The awkward parts, and how they are handled here:
//
//    * Capture is loopback, not a microphone. ScreenCaptureKit is the only public way to get the system's output,
//      it needs the Screen Recording grant rather than the microphone one, and it is the same grant the display
//      capture already asks for. `excludesCurrentProcessAudio` matters more than it looks: this process plays the
//      far end's audio, so capturing our own output would close a feedback loop.
//    * ScreenCaptureKit hands audio back as an AudioBufferList that may be interleaved (one buffer) or not (one
//      buffer per channel) depending on the format it chose, so both layouts are flattened to interleaved here.
//      The port contract says interleaved, and doing it in one place keeps the managed side from caring.
//    * Playback cannot use the "queue it and let the device deal with it" model, because the port contract wants a
//      backlog to be *dropped* rather than played late. AudioQueue has no way to remove one already-queued buffer,
//      so the audio is held in a ring this file owns and the AudioQueue callback only ever drains from it. Dropping
//      the oldest is then a matter of moving the read index.
//    * An underrun plays silence rather than stopping: the callback fills the remainder of the buffer with zeros,
//      which is what the Windows player's ReadFully does too.
//

#import "rd_native.h"

#import <AudioToolbox/AudioToolbox.h>
#import <CoreGraphics/CoreGraphics.h>
#import <CoreMedia/CoreMedia.h>
#import <Foundation/Foundation.h>
#import <ScreenCaptureKit/ScreenCaptureKit.h>
#import <os/lock.h>

/// The one format the wire carries, so nothing resamples anywhere.
static const int32_t RDAudioSampleRate = 48000;
static const int32_t RDAudioChannels = 2;

/// Fills the caller's message buffer, tolerating a null or zero-length buffer.
static void RDAudioSetError(char *error, size_t errorSize, NSString *message)
{
    if (error == NULL || errorSize == 0) {
        return;
    }

    const char *utf8 = message.UTF8String;
    if (utf8 == NULL) {
        error[0] = '\0';
        return;
    }

    strncpy(error, utf8, errorSize - 1);
    error[errorSize - 1] = '\0';
}

/// Maps an NSError from ScreenCaptureKit onto one of our status codes.
static int RDAudioStatusForError(NSError *error)
{
    if (error == nil) {
        return RD_OK;
    }

    if ([error.domain isEqualToString:SCStreamErrorDomain]) {
        switch (error.code) {
            case SCStreamErrorUserDeclined:
            case SCStreamErrorNoCaptureSource:
                return RD_ERROR_PERMISSION;
            default:
                break;
        }
    }

    return RD_ERROR_STREAM;
}

#pragma mark - Capture

/// Receives the stream's audio and forwards it as interleaved float.
@interface RDAudioCapture : NSObject <SCStreamOutput, SCStreamDelegate>
@property (nonatomic, strong) SCContentFilter *filter;
@property (nonatomic, strong) SCStreamConfiguration *configuration;
@property (nonatomic, strong) SCStream *stream;
@property (nonatomic, strong) dispatch_queue_t queue;
@property (nonatomic, assign) rd_audio_callback callback;
@property (nonatomic, assign) void *user;
@property (nonatomic, assign) BOOL started;

/// Scratch for the interleaved result, reused across callbacks. Only ever read inside the callback, and the header
/// says the pointer is valid for the callback only, so one buffer is enough.
@property (nonatomic, assign) float *interleaved;
@property (nonatomic, assign) size_t interleavedCapacity;
@end

@implementation RDAudioCapture

/// Flattens an AudioBufferList to interleaved float, or returns NULL when the layout is not one this can read.
///
/// Returns the number of frames written.
- (int32_t)flatten:(AudioBufferList *)buffers
        description:(const AudioStreamBasicDescription *)format
           channels:(int32_t)channels
              frames:(int32_t)frames
{
    const size_t needed = (size_t)frames * (size_t)channels;
    if (needed == 0) {
        return 0;
    }

    if (self.interleavedCapacity < needed) {
        float *grown = realloc(self.interleaved, needed * sizeof(float));
        if (grown == NULL) {
            return 0;
        }

        self.interleaved = grown;
        self.interleavedCapacity = needed;
    }

    const BOOL nonInterleaved = (format->mFormatFlags & kAudioFormatFlagIsNonInterleaved) != 0;

    if (nonInterleaved) {
        // One buffer per channel: gather them into the interleaved layout the contract asks for.
        if (buffers->mNumberBuffers < (UInt32)channels) {
            return 0;
        }

        for (int32_t channel = 0; channel < channels; channel++) {
            const AudioBuffer *source = &buffers->mBuffers[channel];
            if (source->mData == NULL) {
                return 0;
            }

            const float *samples = (const float *)source->mData;
            for (int32_t frame = 0; frame < frames; frame++) {
                self.interleaved[((size_t)frame * channels) + channel] = samples[frame];
            }
        }
    } else {
        // Already interleaved, which is the common case for the format this stream is configured with.
        const AudioBuffer *source = &buffers->mBuffers[0];
        if (source->mData == NULL) {
            return 0;
        }

        memcpy(self.interleaved, source->mData, needed * sizeof(float));
    }

    return frames;
}

- (void)stream:(SCStream *)stream didOutputSampleBuffer:(CMSampleBufferRef)sampleBuffer ofType:(SCStreamOutputType)type
{
    if (type != SCStreamOutputTypeAudio || self.callback == NULL) {
        return;
    }

    CMFormatDescriptionRef description = CMSampleBufferGetFormatDescription(sampleBuffer);
    if (description == NULL) {
        return;
    }

    const AudioStreamBasicDescription *format = CMAudioFormatDescriptionGetStreamBasicDescription(description);
    if (format == NULL) {
        return;
    }

    // Anything that is not float cannot be handed over without a conversion this layer does not do. The stream is
    // configured for 48 kHz stereo float, so this is a guard rather than a case that should ever fire — and
    // delivering misinterpreted samples would be worse than delivering nothing.
    if ((format->mFormatFlags & kAudioFormatFlagIsFloat) == 0) {
        return;
    }

    const int32_t channels = (int32_t)format->mChannelsPerFrame;
    const int32_t frames = (int32_t)CMSampleBufferGetNumSamples(sampleBuffer);
    if (channels <= 0 || frames <= 0) {
        return;
    }

    // The list is variable-length, so room for one AudioBuffer per channel has to be reserved by hand.
    const size_t listSize = offsetof(AudioBufferList, mBuffers) + (sizeof(AudioBuffer) * (size_t)channels);
    AudioBufferList *buffers = (AudioBufferList *)malloc(listSize);
    if (buffers == NULL) {
        return;
    }

    CMBlockBufferRef block = NULL;
    const OSStatus status = CMSampleBufferGetAudioBufferListWithRetainedBlockBuffer(
        sampleBuffer,
        NULL,
        buffers,
        listSize,
        NULL,
        NULL,
        kCMSampleBufferFlag_AudioBufferList_Assure16ByteAlignment,
        &block);

    if (status == noErr) {
        const int32_t written = [self flatten:buffers description:format channels:channels frames:frames];
        if (written > 0) {
            rd_audio_chunk chunk = {
                .samples = self.interleaved,
                .frame_count = written,
                .channel_count = channels,
                .sample_rate = (int32_t)format->mSampleRate,
            };

            self.callback(self.user, &chunk);
        }
    }

    if (block != NULL) {
        CFRelease(block);
    }

    free(buffers);
}

- (void)stream:(SCStream *)stream didStopWithError:(NSError *)error
{
    // Nothing to report: the managed side notices a capture that has stopped because chunks simply stop arriving,
    // and a device change is handled by starting a new capture.
    (void)error;
}

@end

int rd_audio_capture_create(rd_audio_callback callback,
                            void *user,
                            rd_audio_capture_ref *out,
                            char *error,
                            size_t error_size)
{
    if (callback == NULL || out == NULL) {
        RDAudioSetError(error, error_size, @"rd_audio_capture_create needs an output handle and a callback.");
        return RD_ERROR_ARGUMENT;
    }

    *out = NULL;

    @autoreleasepool {
        __block SCShareableContent *content = nil;
        __block NSError *lookupError = nil;

        // Blocking here for the same reason the display capture does: the alternative is a two-phase construction
        // in every caller. The timeout is a net for a ScreenCaptureKit that never answers.
        dispatch_semaphore_t ready = dispatch_semaphore_create(0);
        [SCShareableContent getShareableContentWithCompletionHandler:^(SCShareableContent *result, NSError *err) {
            content = result;
            lookupError = err;
            dispatch_semaphore_signal(ready);
        }];

        if (dispatch_semaphore_wait(ready, dispatch_time(DISPATCH_TIME_NOW, (int64_t)(10 * NSEC_PER_SEC))) != 0) {
            RDAudioSetError(error, error_size, @"ScreenCaptureKit did not answer the shareable-content request in time.");
            return RD_ERROR_TIMEOUT;
        }

        if (content == nil) {
            RDAudioSetError(error, error_size, lookupError.localizedDescription ?: @"No shareable content is available.");
            return RDAudioStatusForError(lookupError);
        }

        // ScreenCaptureKit has no audio-only filter: a stream always has to be anchored to something on screen.
        // The display is therefore only there to satisfy the API, and no screen output is added, so no video is
        // ever produced from it.
        SCDisplay *display = nil;
        for (SCDisplay *candidate in content.displays) {
            if (candidate.displayID == CGMainDisplayID()) {
                display = candidate;
                break;
            }
        }

        if (display == nil) {
            display = content.displays.firstObject;
        }

        if (display == nil) {
            RDAudioSetError(error, error_size, @"No display is available to anchor the audio stream to.");
            return RD_ERROR_DISPLAY_NOT_FOUND;
        }

        RDAudioCapture *capture = [[RDAudioCapture alloc] init];
        capture.filter = [[SCContentFilter alloc] initWithDisplay:display excludingWindows:@[]];

        SCStreamConfiguration *configuration = [[SCStreamConfiguration alloc] init];
        configuration.capturesAudio = YES;

        // This process plays the far end's audio. Capturing our own output back would send it straight back to
        // where it came from, so it is excluded — the loop is otherwise inaudible until it is deafening.
        configuration.excludesCurrentProcessAudio = YES;

        // Fixed at what the wire carries, so nothing has to resample on either side.
        configuration.sampleRate = RDAudioSampleRate;
        configuration.channelCount = RDAudioChannels;

        // A stream needs a valid size even when no screen output is attached.
        configuration.width = (size_t)display.width;
        configuration.height = (size_t)display.height;

        capture.configuration = configuration;

        // A serial queue is what makes the callback single-threaded from the callee's point of view.
        capture.queue = dispatch_queue_create("com.remotedesktop.audio-capture", DISPATCH_QUEUE_SERIAL);

        capture.stream = [[SCStream alloc] initWithFilter:capture.filter
                                            configuration:configuration
                                                 delegate:capture];

        NSError *addError = nil;
        if (![capture.stream addStreamOutput:capture
                                        type:SCStreamOutputTypeAudio
                          sampleHandlerQueue:capture.queue
                                       error:&addError]) {
            RDAudioSetError(error, error_size, addError.localizedDescription ?: @"The stream rejected the audio output.");
            return RDAudioStatusForError(addError);
        }

        capture.callback = callback;
        capture.user = user;

        *out = (__bridge_retained void *)capture;
        return RD_OK;
    }
}

int rd_audio_capture_start(rd_audio_capture_ref handle, char *error, size_t error_size)
{
    RDAudioCapture *capture = (__bridge RDAudioCapture *)handle;
    if (capture == nil) {
        RDAudioSetError(error, error_size, @"The audio capture handle is not valid.");
        return RD_ERROR_ARGUMENT;
    }

    if (capture.started) {
        return RD_OK;
    }

    @autoreleasepool {
        __block NSError *startError = nil;
        dispatch_semaphore_t started = dispatch_semaphore_create(0);

        [capture.stream startCaptureWithCompletionHandler:^(NSError *err) {
            startError = err;
            dispatch_semaphore_signal(started);
        }];

        if (dispatch_semaphore_wait(started, dispatch_time(DISPATCH_TIME_NOW, (int64_t)(10 * NSEC_PER_SEC))) != 0) {
            RDAudioSetError(error, error_size, @"The audio stream did not start in time.");
            return RD_ERROR_TIMEOUT;
        }

        if (startError != nil) {
            RDAudioSetError(error, error_size, startError.localizedDescription ?: @"The audio stream failed to start.");
            return RDAudioStatusForError(startError);
        }

        capture.started = YES;
        return RD_OK;
    }
}

void rd_audio_capture_stop(rd_audio_capture_ref handle)
{
    RDAudioCapture *capture = (__bridge RDAudioCapture *)handle;
    if (capture == nil || !capture.started) {
        return;
    }

    capture.started = NO;

    // Stopping is asynchronous, but a caller that asked to stop needs it stopped when this returns.
    dispatch_semaphore_t stopped = dispatch_semaphore_create(0);
    [capture.stream stopCaptureWithCompletionHandler:^(NSError *err) {
        (void)err;
        dispatch_semaphore_signal(stopped);
    }];
    dispatch_semaphore_wait(stopped, dispatch_time(DISPATCH_TIME_NOW, (int64_t)(5 * NSEC_PER_SEC)));
}

void rd_audio_capture_destroy(rd_audio_capture_ref handle)
{
    if (handle == NULL) {
        return;
    }

    // Take ownership back from the bridge; ARC releases it at the end of this scope.
    RDAudioCapture *capture = (__bridge_transfer RDAudioCapture *)handle;
    rd_audio_capture_stop((__bridge void *)capture);

    // Clearing the callback before the stream is released closes the window where a chunk in flight could still
    // reach managed memory that is being torn down.
    capture.callback = NULL;
    capture.user = NULL;
    capture.stream = nil;
    capture.filter = nil;
    capture.configuration = nil;

    free(capture.interleaved);
    capture.interleaved = NULL;
    capture.interleavedCapacity = 0;
}

#pragma mark - Playback

/// One playback device, with the ring the audio waits in.
@interface RDAudioPlayer : NSObject
@property (nonatomic, assign) AudioQueueRef queue;
@property (nonatomic, assign) AudioStreamBasicDescription format;
/// Interleaved float samples, `capacity` of them.
@property (nonatomic, assign) float *ring;
@property (nonatomic, assign) size_t capacity;
@property (nonatomic, assign) size_t readIndex;
@property (nonatomic, assign) size_t count;
@property (nonatomic, assign) os_unfair_lock lock;
@property (nonatomic, assign) BOOL started;
@end

@implementation RDAudioPlayer

/// Takes up to `wanted` samples out of the ring into `destination`, returning how many were written.
- (size_t)drain:(float *)destination wanted:(size_t)wanted
{
    os_unfair_lock_lock(&self->_lock);

    const size_t take = self.count < wanted ? self.count : wanted;
    const size_t first = self.capacity - self.readIndex;
    const size_t head = take < first ? take : first;

    memcpy(destination, self.ring + self.readIndex, head * sizeof(float));
    if (take > head) {
        memcpy(destination + head, self.ring, (take - head) * sizeof(float));
    }

    self.readIndex = (self.readIndex + take) % self.capacity;
    self.count -= take;

    os_unfair_lock_unlock(&self->_lock);
    return take;
}

/// Appends `count` interleaved samples, dropping the oldest audio when the ring cannot hold them all.
///
/// A method rather than open-coded in the entry point for the reason the ring exists at all: the queue callback
/// thread runs through the same fields, and every one of those transitions has to happen under the lock. Keeping
/// them in one place is what makes that checkable.
- (void)enqueue:(const float *)samples count:(size_t)sampleCount
{
    os_unfair_lock_lock(&self->_lock);

    // More than the ring holds means the device is far behind; keeping the newest audio is the point of the ring,
    // so only the tail is kept.
    if (sampleCount >= self.capacity) {
        memcpy(self.ring, samples + (sampleCount - self.capacity), self.capacity * sizeof(float));
        self.readIndex = 0;
        self.count = self.capacity;
        os_unfair_lock_unlock(&self->_lock);
        return;
    }

    // Make room by dropping the oldest audio, which is what bounds latency instead of growing the backlog.
    //
    // Written as an explicit comparison rather than `count + sampleCount - capacity`: all three are size_t, so on
    // the ordinary path — where the sum is *below* capacity — that subtraction wraps to about 1.8e19, the branch
    // below fires on every enqueue, and the read index is dragged somewhere meaningless. The ring then reports a
    // count that has nothing to do with what was put in, which is how this was found: the C probe showed 4800
    // samples going in and something else coming back.
    const size_t total = self.count + sampleCount;
    if (total > self.capacity) {
        const size_t overflow = total - self.capacity;
        self.readIndex = (self.readIndex + overflow) % self.capacity;
        self.count -= overflow;
    }

    const size_t writeIndex = (self.readIndex + self.count) % self.capacity;
    const size_t first = self.capacity - writeIndex;
    const size_t head = sampleCount < first ? sampleCount : first;

    memcpy(self.ring + writeIndex, samples, head * sizeof(float));
    if (sampleCount > head) {
        memcpy(self.ring, samples + head, (sampleCount - head) * sizeof(float));
    }

    self.count += sampleCount;

    os_unfair_lock_unlock(&self->_lock);
}

/// How many samples are waiting to be played.
- (int64_t)buffered
{
    os_unfair_lock_lock(&self->_lock);
    const size_t count = self.count;
    os_unfair_lock_unlock(&self->_lock);
    return (int64_t)count;
}

/// Throws away everything waiting to be played, without touching the device.
- (void)discard
{
    os_unfair_lock_lock(&self->_lock);
    self.readIndex = 0;
    self.count = 0;
    os_unfair_lock_unlock(&self->_lock);
}

@end

/// Fills a queue buffer from the ring, padding with silence, and hands it straight back.
///
/// Runs on AudioQueue's own thread, so it must not block and must not throw.
static void RDAudioQueueCallback(void *user, AudioQueueRef queue, AudioQueueBufferRef buffer)
{
    RDAudioPlayer *player = (__bridge RDAudioPlayer *)user;
    if (player == nil || buffer->mAudioData == NULL) {
        return;
    }

    const size_t capacitySamples = buffer->mAudioDataBytesCapacity / sizeof(float);
    const size_t taken = [player drain:(float *)buffer->mAudioData wanted:capacitySamples];

    // The remainder is silence, which makes an underrun a pause rather than a gap of whatever was in the buffer
    // last time. This is the same choice the Windows player makes by reading fully from its buffer.
    if (taken < capacitySamples) {
        memset((float *)buffer->mAudioData + taken, 0, (capacitySamples - taken) * sizeof(float));
    }

    buffer->mAudioDataByteSize = (UInt32)(capacitySamples * sizeof(float));
    AudioQueueEnqueueBuffer(queue, buffer, 0, NULL);
}

int rd_audio_player_create(int32_t sample_rate,
                           int32_t channel_count,
                           rd_audio_player_ref *out,
                           char *error,
                           size_t error_size)
{
    if (out == NULL) {
        RDAudioSetError(error, error_size, @"rd_audio_player_create needs an output handle.");
        return RD_ERROR_ARGUMENT;
    }

    *out = NULL;

    if (sample_rate <= 0 || channel_count <= 0) {
        RDAudioSetError(error, error_size, @"The audio format must have a positive rate and channel count.");
        return RD_ERROR_ARGUMENT;
    }

    @autoreleasepool {
        RDAudioPlayer *player = [[RDAudioPlayer alloc] init];

        AudioStreamBasicDescription format = {0};
        format.mSampleRate = sample_rate;
        format.mFormatID = kAudioFormatLinearPCM;
        format.mFormatFlags = kAudioFormatFlagIsFloat | kAudioFormatFlagIsPacked;
        format.mChannelsPerFrame = (UInt32)channel_count;
        format.mBitsPerChannel = 32;
        format.mFramesPerPacket = 1;
        format.mBytesPerFrame = (UInt32)(sizeof(float) * channel_count);
        format.mBytesPerPacket = format.mBytesPerFrame;
        player.format = format;

        // One second of slack, matching the buffer the Windows player allocates. Anything beyond this is a device
        // that has stopped consuming, and the oldest audio is dropped rather than growing the wait.
        player.capacity = (size_t)sample_rate * (size_t)channel_count;
        player.ring = calloc(player.capacity, sizeof(float));
        if (player.ring == NULL) {
            RDAudioSetError(error, error_size, @"Out of memory for the audio buffer.");
            return RD_ERROR_UNAVAILABLE;
        }

        player.lock = OS_UNFAIR_LOCK_INIT;
        player.readIndex = 0;
        player.count = 0;

        // A local, not &player.queue: AudioQueueNewOutput wants the address of the reference, and a property
        // expression has no address to give it.
        AudioQueueRef queue = NULL;
        OSStatus status = AudioQueueNewOutput(&format, RDAudioQueueCallback, (__bridge void *)player, NULL, NULL, 0, &queue);
        if (status != noErr || queue == NULL) {
            free(player.ring);
            player.ring = NULL;
            RDAudioSetError(error, error_size, [NSString stringWithFormat:@"No audio output device could be opened (OSStatus %d).", (int)status]);
            return RD_ERROR_AUDIO_DEVICE;
        }

        player.queue = queue;

        // Three short buffers: enough to ride out scheduling jitter, short enough that a pause is noticed quickly.
        const UInt32 bufferBytes = (UInt32)((sample_rate / 50) * format.mBytesPerFrame);
        for (int i = 0; i < 3; i++) {
            AudioQueueBufferRef buffer = NULL;
            status = AudioQueueAllocateBuffer(player.queue, bufferBytes, &buffer);
            if (status != noErr || buffer == NULL) {
                AudioQueueDispose(player.queue, true);
                player.queue = NULL;
                free(player.ring);
                player.ring = NULL;
                RDAudioSetError(error, error_size, @"The audio device refused its buffers.");
                return RD_ERROR_AUDIO_DEVICE;
            }

            memset(buffer->mAudioData, 0, buffer->mAudioDataBytesCapacity);
            buffer->mAudioDataByteSize = buffer->mAudioDataBytesCapacity;
            AudioQueueEnqueueBuffer(player.queue, buffer, 0, NULL);
        }

        *out = (__bridge_retained void *)player;
        return RD_OK;
    }
}

int rd_audio_player_start(rd_audio_player_ref handle, char *error, size_t error_size)
{
    RDAudioPlayer *player = (__bridge RDAudioPlayer *)handle;
    if (player == nil) {
        RDAudioSetError(error, error_size, @"The audio player handle is not valid.");
        return RD_ERROR_ARGUMENT;
    }

    if (player.started) {
        return RD_OK;
    }

    const OSStatus status = AudioQueueStart(player.queue, NULL);
    if (status != noErr) {
        RDAudioSetError(error, error_size, [NSString stringWithFormat:@"The audio device would not start (OSStatus %d).", (int)status]);
        return RD_ERROR_AUDIO_DEVICE;
    }

    player.started = YES;
    return RD_OK;
}

int rd_audio_player_enqueue(rd_audio_player_ref handle,
                            const float *samples,
                            size_t sample_count,
                            char *error,
                            size_t error_size)
{
    RDAudioPlayer *player = (__bridge RDAudioPlayer *)handle;
    if (player == nil) {
        RDAudioSetError(error, error_size, @"The audio player handle is not valid.");
        return RD_ERROR_ARGUMENT;
    }

    if (samples == NULL || sample_count == 0) {
        return RD_OK;
    }

    [player enqueue:samples count:sample_count];
    return RD_OK;
}

int64_t rd_audio_player_buffered(rd_audio_player_ref handle)
{
    RDAudioPlayer *player = (__bridge RDAudioPlayer *)handle;
    if (player == nil) {
        return -1;
    }

    return [player buffered];
}

void rd_audio_player_stop(rd_audio_player_ref handle)
{
    RDAudioPlayer *player = (__bridge RDAudioPlayer *)handle;
    if (player == nil || !player.started) {
        return;
    }

    player.started = NO;

    // true: stop now and wait, so that when this returns the callback cannot be running any more.
    AudioQueueStop(player.queue, true);

    // Whatever was queued is not going to be played.
    [player discard];
}

void rd_audio_player_discard(rd_audio_player_ref handle)
{
    RDAudioPlayer *player = (__bridge RDAudioPlayer *)handle;
    if (player == nil) {
        return;
    }

    [player discard];
}

void rd_audio_player_destroy(rd_audio_player_ref handle)
{
    if (handle == NULL) {
        return;
    }

    // Take ownership back from the bridge; ARC releases it at the end of this scope.
    RDAudioPlayer *player = (__bridge_transfer RDAudioPlayer *)handle;
    rd_audio_player_stop((__bridge void *)player);

    if (player.queue != NULL) {
        // true: dispose immediately. Safe after the stop above, which already waited for the callback.
        AudioQueueDispose(player.queue, true);
        player.queue = NULL;
    }

    free(player.ring);
    player.ring = NULL;
    player.capacity = 0;
}
