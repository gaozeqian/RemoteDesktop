#import "rd_video_common.h"

#import <dispatch/dispatch.h>
#import <os/lock.h>
#import <stdlib.h>
#import <string.h>

// Bounds the wait for the current picture when the caller passes something unreasonable. The managed layer always
// passes a value derived from its frame rate, so this only guards against a caller that does not.
#define RD_VIDEO_DEFAULT_TIMEOUT_MS 1000

#pragma mark - Encoder

/// One pending access unit. It owns its bytes because VideoToolbox's block buffer is only valid inside the callback.
typedef struct {
    uint8_t *data;
    size_t length;
    int is_key_frame;
} rd_video_queue_item;

/// A VideoToolbox compression session, plus the queue of finished access units.
///
/// The queue rather than the callback is what the entry points read, and that split is the point: VideoToolbox
/// calls back on its own thread at a moment of its choosing, while the caller wants "give me what is ready now".
/// A queue in between gives both without the managed side ever learning about CoreMedia.
@interface RDVideoEncoder : NSObject

@property (nonatomic, assign) VTCompressionSessionRef session;
@property (nonatomic, assign) rd_video_encoder_callback callback;
@property (nonatomic, assign) void *user;
@property (nonatomic, assign) int32_t width;
@property (nonatomic, assign) int32_t height;
@property (nonatomic, assign) int32_t frameRate;

/// Finished access units waiting to be handed out. Guarded by `lock`, because the output callback runs on
/// VideoToolbox's thread while the entry point drains from the caller's.
@property (nonatomic, assign) rd_video_queue_item *queue;
@property (nonatomic, assign) int32_t queueCount;
@property (nonatomic, assign) int32_t queueCapacity;
@property (nonatomic, assign) os_unfair_lock lock;
@property (nonatomic, strong) dispatch_semaphore_t signal;

/// A failure VideoToolbox reported through the callback. Without this a failed encode looks exactly like an
/// encode that simply has not produced anything yet, which is indistinguishable from the outside.
@property (nonatomic, assign) OSStatus lastStatus;

@end

@implementation RDVideoEncoder

/// Appends one access unit. Called from VideoToolbox's callback thread.
- (void)append:(const uint8_t *)data length:(size_t)length isKeyFrame:(BOOL)isKeyFrame
{
    uint8_t *copy = malloc(length);
    if (copy == NULL) {
        return;
    }
    memcpy(copy, data, length);

    os_unfair_lock_lock(&self->_lock);

    if (self.queueCount == self.queueCapacity) {
        const int32_t grown = self.queueCapacity == 0 ? 8 : self.queueCapacity * 2;
        rd_video_queue_item *bigger = realloc(self.queue, (size_t)grown * sizeof(*bigger));
        if (bigger == NULL) {
            os_unfair_lock_unlock(&self->_lock);
            free(copy);
            return;
        }
        self.queue = bigger;
        self.queueCapacity = grown;
    }

    self.queue[self.queueCount].data = copy;
    self.queue[self.queueCount].length = length;
    self.queue[self.queueCount].is_key_frame = isKeyFrame;
    self.queueCount++;

    os_unfair_lock_unlock(&self->_lock);
    dispatch_semaphore_signal(self.signal);
}

/// Takes at most `capacity` access units into `destination`, returning how many were written.
///
/// The bytes are handed over here, so each entry is cleared: after this the caller owns them and the queue must not
/// free them a second time on teardown.
- (int32_t)drainInto:(rd_video_packet *)destination capacity:(int32_t)capacity
{
    os_unfair_lock_lock(&self->_lock);

    const int32_t taken = self.queueCount < capacity ? self.queueCount : capacity;
    for (int32_t i = 0; i < taken; i++) {
        destination[i].data = (const uint8_t *)self.queue[i].data;
        destination[i].length = self.queue[i].length;
        destination[i].is_key_frame = self.queue[i].is_key_frame;
        self.queue[i].data = NULL;
    }

    // Compact what is left, preserving order: a late output must not overtake the current picture.
    if (taken > 0) {
        memmove(self.queue, self.queue + taken, (size_t)(self.queueCount - taken) * sizeof(*self.queue));
        self.queueCount -= taken;
    }

    os_unfair_lock_unlock(&self->_lock);
    return taken;
}

/// Waits until at least one access unit is queued. `YES` means there is something to take.
///
/// A loop rather than one wait, to swallow spurious wake-ups: a signal without a queued packet would otherwise
/// make the caller believe this frame produced output when it did not.
- (BOOL)waitForPacketWithTimeoutMs:(int32_t)timeoutMs
{
    const dispatch_time_t deadline = dispatch_time(DISPATCH_TIME_NOW, (int64_t)timeoutMs * NSEC_PER_MSEC);
    for (;;) {
        os_unfair_lock_lock(&self->_lock);
        const BOOL ready = self.queueCount > 0;
        os_unfair_lock_unlock(&self->_lock);

        if (ready) {
            return YES;
        }

        // Once the deadline has passed dispatch_semaphore_wait returns non-zero immediately, so this terminates.
        if (dispatch_semaphore_wait(self.signal, deadline) != 0) {
            return NO;
        }
    }
}

/// Records a failure VideoToolbox reported through the callback, and wakes any waiter.
- (void)recordStatus:(OSStatus)status
{
    if (status == noErr) {
        return;
    }

    os_unfair_lock_lock(&self->_lock);
    self.lastStatus = status;
    os_unfair_lock_unlock(&self->_lock);
    dispatch_semaphore_signal(self.signal);
}

/// Takes and clears the recorded failure.
- (OSStatus)takeLastStatus
{
    os_unfair_lock_lock(&self->_lock);
    const OSStatus status = self.lastStatus;
    self.lastStatus = noErr;
    os_unfair_lock_unlock(&self->_lock);
    return status;
}

/// Releases every queued packet's bytes. Safe to call more than once.
- (void)releaseQueue
{
    os_unfair_lock_lock(&self->_lock);
    for (int32_t i = 0; i < self.queueCount; i++) {
        free(self.queue[i].data);
        self.queue[i].data = NULL;
    }
    self.queueCount = 0;
    os_unfair_lock_unlock(&self->_lock);
}

/// Releases the queue's backing store.
///
/// The pragma around it is not hiding a mistake: under ARC a `dealloc` must not call `[super dealloc]`, but clang
/// still warns when one frees C memory instead. There is nothing for it to call, so the warning is silenced rather
/// than answered.
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wobjc-missing-super-calls"
- (void)dealloc
{
    [self releaseQueue];
    free(self.queue);
}
#pragma clang diagnostic pop

@end

#pragma mark - Callback

/// VideoToolbox tells us a picture is ready, on a thread of its own.
///
/// Everything below is copy-and-convert; nothing here blocks, so the encoder's own thread stays free.
static void RDVideoEncoderOutput(void *refcon, void *sourceFrameRefcon, OSStatus status, VTEncodeInfoFlags infoFlags, CMSampleBufferRef sampleBuffer)
{
    (void)sourceFrameRefcon;
    (void)infoFlags;

    RDVideoEncoder *encoder = (__bridge RDVideoEncoder *)refcon;
    if (encoder == nil) {
        return;
    }

    if (status != noErr || sampleBuffer == NULL) {
        [encoder recordStatus:status == noErr ? -1 : status];
        return;
    }

    CMFormatDescriptionRef description = CMSampleBufferGetFormatDescription(sampleBuffer);
    CMBlockBufferRef block = CMSampleBufferGetDataBuffer(sampleBuffer);
    if (description == NULL || block == NULL) {
        [encoder recordStatus:-1];
        return;
    }

    const size_t totalLength = CMBlockBufferGetDataLength(block);
    if (totalLength == 0) {
        return;
    }

    // Pointer into the AVCC data. The two length parameters are outputs, not the offset and length: passing the
    // length itself there is a type error that also happens to be accepted as a pointer on arm64, which is the
    // dangerous kind of mistake.
    char *avcc = NULL;
    BOOL ownsAvcc = NO;
    if (CMBlockBufferGetDataPointer(block, 0, NULL, NULL, &avcc) != kCMBlockBufferNoErr || avcc == NULL) {
        avcc = malloc(totalLength);
        if (avcc == NULL) {
            [encoder recordStatus:-1];
            return;
        }
        ownsAvcc = YES;
        if (CMBlockBufferCopyDataBytes(block, 0, totalLength, avcc) != kCMBlockBufferNoErr) {
            free(avcc);
            [encoder recordStatus:-1];
            return;
        }
    }

    // One pass to count the NALs and learn what is in the access unit. Both facts are needed: the count sizes the
    // Annex-B buffer, and whether the picture is an IDR decides whether parameter sets have to be prepended.
    size_t nalCount = 0;
    int hasSps = 0, hasPps = 0, hasIdr = 0;
    {
        size_t position = 0;
        while (position + 4 <= totalLength) {
            const uint32_t nalLength = ((uint32_t)(uint8_t)avcc[position] << 24)
                | ((uint32_t)(uint8_t)avcc[position + 1] << 16)
                | ((uint32_t)(uint8_t)avcc[position + 2] << 8)
                | (uint32_t)(uint8_t)avcc[position + 3];
            position += 4;

            if (nalLength == 0 || position + nalLength > totalLength) {
                break;
            }

            const int type = (uint8_t)avcc[position] & 0x1F;
            if (type == RD_H264_NAL_SPS) {
                hasSps = 1;
            } else if (type == RD_H264_NAL_PPS) {
                hasPps = 1;
            } else if (type == RD_H264_NAL_IDR) {
                hasIdr = 1;
            }
            nalCount++;

            position += nalLength;
        }
    }

    if (nalCount == 0) {
        if (ownsAvcc) {
            free(avcc);
        }
        return;
    }

    size_t parameterSetBytes = 0;
    if (hasIdr && !(hasSps && hasPps)) {
        parameterSetBytes = RDVideoParameterSetTotalSize(description);
    }

    // Four bytes of start code per NAL, plus whatever the parameter sets need.
    const size_t capacity = totalLength + (4 * nalCount) + parameterSetBytes;
    uint8_t *annexB = malloc(capacity);
    if (annexB == NULL) {
        if (ownsAvcc) {
            free(avcc);
        }
        [encoder recordStatus:-1];
        return;
    }

    size_t written = parameterSetBytes > 0 ? RDVideoAppendParameterSets(description, annexB, capacity) : 0;
    written += RDVideoAvccToAnnexB((const uint8_t *)avcc, totalLength, annexB + written, capacity - written);

    if (ownsAvcc) {
        free(avcc);
    }

    if (written > 0) {
        [encoder append:annexB length:written isKeyFrame:hasIdr];
    }

    free(annexB);
}

#pragma mark - Selection

/// Picks an encoder out of VTCopyVideoEncoderList and wraps its identifier into a session specification.
///
/// Returns NULL when nothing matches, which lets VideoToolbox choose its own default rather than failing — the
/// right behaviour when a caller asks for software on a machine that only has hardware.
static CFDictionaryRef RDVideoSelectEncoder(CMVideoCodecType codec, BOOL preferSoftware, char *nameOut, size_t nameSize, BOOL *isHardwareOut)
{
    if (nameOut != NULL && nameSize > 0) {
        nameOut[0] = '\0';
    }
    if (isHardwareOut != NULL) {
        *isHardwareOut = NO;
    }

    CFArrayRef list = NULL;
    if (VTCopyVideoEncoderList(NULL, &list) != noErr || list == NULL) {
        return NULL;
    }

    CFDictionaryRef specification = NULL;
    const BOOL wantHardware = !preferSoftware;
    const CFIndex count = CFArrayGetCount(list);

    for (CFIndex i = 0; i < count; i++) {
        CFDictionaryRef entry = (CFDictionaryRef)CFArrayGetValueAtIndex(list, i);
        if (entry == NULL) {
            continue;
        }

        CFNumberRef codecNumber = (CFNumberRef)CFDictionaryGetValue(entry, kVTVideoEncoderList_CodecType);
        uint32_t entryCodec = 0;
        if (codecNumber != NULL) {
            CFNumberGetValue(codecNumber, kCFNumberSInt32Type, &entryCodec);
        }
        if (entryCodec != codec) {
            continue;
        }

        CFBooleanRef hardware = (CFBooleanRef)CFDictionaryGetValue(entry, kVTVideoEncoderList_IsHardwareAccelerated);
        // An entry that does not say is treated as "not what was asked for": guessing wrong here would silently
        // hand back the other encoder, which is the whole thing the flag exists to avoid.
        const BOOL entryIsHardware = hardware != NULL && CFBooleanGetValue(hardware);
        if (entryIsHardware != wantHardware) {
            continue;
        }

        CFStringRef encoderId = (CFStringRef)CFDictionaryGetValue(entry, kVTVideoEncoderList_EncoderID);
        if (encoderId == NULL) {
            continue;
        }

        CFStringRef encoderName = (CFStringRef)CFDictionaryGetValue(entry, kVTVideoEncoderList_EncoderName);
        if (nameOut != NULL && nameSize > 0 && encoderName != NULL) {
            CFStringGetCString(encoderName, nameOut, nameSize, kCFStringEncodingUTF8);
        }
        if (isHardwareOut != NULL) {
            *isHardwareOut = entryIsHardware;
        }

        specification = CFDictionaryCreate(
            NULL,
            (const void **)&kVTVideoEncoderSpecification_EncoderID,
            (const void **)&encoderId,
            1,
            &kCFCopyStringDictionaryKeyCallBacks,
            &kCFTypeDictionaryValueCallBacks);
        break;
    }

    CFRelease(list);
    return specification;
}

static void RDVideoSetNumber(VTSessionRef session, CFStringRef key, int32_t value)
{
    CFNumberRef number = CFNumberCreate(NULL, kCFNumberSInt32Type, &value);
    if (number == NULL) {
        return;
    }
    VTSessionSetProperty(session, key, number);
    CFRelease(number);
}

static void RDVideoSetBoolean(VTSessionRef session, CFStringRef key, BOOL value)
{
    VTSessionSetProperty(session, key, value ? kCFBooleanTrue : kCFBooleanFalse);
}

#pragma mark - Entry points

int rd_video_encoder_create(int32_t codec, int32_t width, int32_t height, int32_t frame_rate, int32_t bitrate_kbps,
                            int32_t prefer_software, rd_video_encoder_callback callback, void *user,
                            rd_video_encoder_ref *out, char *name_out, size_t name_size,
                            int32_t *is_hardware_out, char *error, size_t error_size)
{
    if (out == NULL || callback == NULL) {
        RDVideoSetError(error, error_size, "The encoder needs an output handle and a callback.");
        return RD_ERROR_ARGUMENT;
    }

    *out = NULL;
    if (name_out != NULL && name_size > 0) {
        name_out[0] = '\0';
    }
    if (is_hardware_out != NULL) {
        *is_hardware_out = 0;
    }

    // Even dimensions, and enough of everything else to describe a stream. Reported here rather than on the first
    // frame, where the caller has already committed to a configuration.
    if (width < 2 || height < 2 || (width % 2) != 0 || (height % 2) != 0) {
        RDVideoSetError(error, error_size, "The encoder needs even dimensions of at least 2x2.");
        return RD_ERROR_ARGUMENT;
    }

    if (frame_rate < 1 || bitrate_kbps < 1) {
        RDVideoSetError(error, error_size, "The encoder needs a positive frame rate and bitrate.");
        return RD_ERROR_ARGUMENT;
    }

    char name[256] = {0};
    BOOL isHardware = NO;
    CFDictionaryRef specification = RDVideoSelectEncoder(
        (CMVideoCodecType)codec, prefer_software != 0, name, sizeof(name), &isHardware);

    int result = RD_OK;
    @autoreleasepool {
        RDVideoEncoder *encoder = [[RDVideoEncoder alloc] init];
        encoder.callback = callback;
        encoder.user = user;
        encoder.width = width;
        encoder.height = height;
        encoder.frameRate = frame_rate;
        encoder.signal = dispatch_semaphore_create(0);

        // A local rather than &encoder.session: the constructor wants the address of the reference, and a property
        // expression has no address to give it.
        VTCompressionSessionRef session = NULL;
        const OSStatus status = VTCompressionSessionCreate(
            NULL, width, height, (CMVideoCodecType)codec, specification, NULL, NULL,
            RDVideoEncoderOutput, (__bridge void *)encoder, &session);

        if (specification != NULL) {
            CFRelease(specification);
        }

        encoder.session = session;

        if (status != noErr || encoder.session == NULL) {
            RDVideoSetError(error, error_size, "No VideoToolbox encoder accepted the configuration.");
            result = RDVideoStatusForError(status);
            return result;
        }

        // Real-time and no reordering together set the latency: the first says "a frame now beats a smaller file",
        // the second rules out B-frames, which is what actually adds pictures of delay.
        RDVideoSetBoolean(encoder.session, kVTCompressionPropertyKey_RealTime, YES);
        RDVideoSetBoolean(encoder.session, kVTCompressionPropertyKey_AllowFrameReordering, NO);
        VTSessionSetProperty(encoder.session, kVTCompressionPropertyKey_ProfileLevel, kVTProfileLevel_H264_High_AutoLevel);
        VTSessionSetProperty(encoder.session, kVTCompressionPropertyKey_H264EntropyMode, kVTH264EntropyMode_CABAC);
        RDVideoSetNumber(encoder.session, kVTCompressionPropertyKey_ExpectedFrameRate, frame_rate);
        RDVideoSetNumber(encoder.session, kVTCompressionPropertyKey_AverageBitRate, bitrate_kbps);
        // Ten seconds of GOP, matching the Media Foundation encoder: recovery is driven by explicit key-frame
        // requests, not by periodic I-frames.
        RDVideoSetNumber(encoder.session, kVTCompressionPropertyKey_MaxKeyFrameInterval, frame_rate * 10);

        if (VTCompressionSessionPrepareToEncodeFrames(encoder.session) != noErr) {
            VTCompressionSessionInvalidate(encoder.session);
            CFRelease(encoder.session);
            encoder.session = NULL;
            RDVideoSetError(error, error_size, "The encoder would not prepare.");
            result = RD_ERROR_CODEC;
            return result;
        }

        if (name_out != NULL && name_size > 0) {
            strncpy(name_out, name, name_size - 1);
            name_out[name_size - 1] = '\0';
        }
        if (is_hardware_out != NULL) {
            *is_hardware_out = isHardware ? 1 : 0;
        }

        *out = (__bridge_retained void *)encoder;
    }

    return result;
}

int rd_video_encoder_set_bitrate(rd_video_encoder_ref encoder_ref, int32_t bitrate_kbps)
{
    if (encoder_ref == NULL || bitrate_kbps < 1) {
        return RD_ERROR_ARGUMENT;
    }

    @autoreleasepool {
        RDVideoEncoder *encoder = (__bridge RDVideoEncoder *)encoder_ref;
        if (encoder.session == NULL) {
            return RD_ERROR_ARGUMENT;
        }

        RDVideoSetNumber(encoder.session, kVTCompressionPropertyKey_AverageBitRate, bitrate_kbps);
        return RD_OK;
    }
}

int rd_video_encoder_encode(rd_video_encoder_ref encoder_ref, const uint8_t *nv12, size_t nv12_size,
                            int32_t force_key_frame, int64_t timestamp_ns,
                            rd_video_packet *output_packets, int32_t output_capacity,
                            int32_t *count_out, int32_t timeout_ms, char *error, size_t error_size)
{
    if (encoder_ref == NULL || nv12 == NULL || output_packets == NULL || count_out == NULL || output_capacity < 1) {
        RDVideoSetError(error, error_size, "Encoding needs an encoder, a frame, and an output array.");
        return RD_ERROR_ARGUMENT;
    }

    *count_out = 0;
    const int32_t effectiveTimeout = timeout_ms > 0 ? timeout_ms : RD_VIDEO_DEFAULT_TIMEOUT_MS;

    int result = RD_OK;
    @autoreleasepool {
        RDVideoEncoder *encoder = (__bridge RDVideoEncoder *)encoder_ref;
        if (encoder.session == NULL) {
            RDVideoSetError(error, error_size, "The encoder session is not open.");
            result = RD_ERROR_ARGUMENT;
            return result;
        }

        if (nv12_size < (size_t)encoder.width * encoder.height * 3 / 2) {
            RDVideoSetError(error, error_size, "The frame is smaller than the encoder's NV12 size.");
            result = RD_ERROR_ARGUMENT;
            return result;
        }

        // Take whatever arrived before this call first. Those are pictures the encoder finished while the caller
        // was busy, and dropping them would desynchronise the decoder's reference pictures.
        int32_t taken = [encoder drainInto:output_packets capacity:output_capacity];
        *count_out = taken;
        if (taken >= output_capacity) {
            // No room left: this frame is not presented. Deliberate rather than fatal — the caller is expected to
            // drain again on its next call, and a frame skipped here costs one picture, not the stream.
            return RD_OK;
        }

        // Two planes, described explicitly. The single-plane bytes-backed constructor cannot be used here: it
        // leaves plane one unset, and the hardware encoder's internal pixel transfer then reads through NULL and
        // dies, with every status reporting success up to that point.
        const size_t yBytes = (size_t)encoder.width * (size_t)encoder.height;
        const size_t planeCount = 2;
        void *planeBaseAddresses[2] = {(void *)nv12, (void *)(nv12 + yBytes)};
        size_t planeWidths[2] = {(size_t)encoder.width, (size_t)encoder.width};
        size_t planeHeights[2] = {(size_t)encoder.height, (size_t)encoder.height / 2};
        size_t planeBytesPerRow[2] = {(size_t)encoder.width, (size_t)encoder.width};

        CVPixelBufferRef pixelBuffer = NULL;
        const CVReturn made = CVPixelBufferCreateWithPlanarBytes(
            NULL, (size_t)encoder.width, (size_t)encoder.height,
            kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange,
            (void *)nv12, nv12_size, planeCount,
            planeBaseAddresses, planeWidths, planeHeights, planeBytesPerRow,
            NULL, NULL, NULL, &pixelBuffer);

        if (made != kCVReturnSuccess || pixelBuffer == NULL) {
            RDVideoSetError(error, error_size, "The frame could not be wrapped for the encoder.");
            result = RD_ERROR_UNAVAILABLE;
            return result;
        }

        CFDictionaryRef frameProperties = NULL;
        if (force_key_frame) {
            frameProperties = CFDictionaryCreate(
                NULL,
                (const void **)&kVTEncodeFrameOptionKey_ForceKeyFrame,
                (const void **)&kCFBooleanTrue,
                1,
                &kCFCopyStringDictionaryKeyCallBacks,
                &kCFTypeDictionaryValueCallBacks);
        }

        const CMTime presentation = CMTimeMake(timestamp_ns, 1000000000);
        const CMTime duration = CMTimeMake(1, (int32_t)encoder.frameRate);

        const OSStatus status = VTCompressionSessionEncodeFrame(
            encoder.session, pixelBuffer, presentation, duration, frameProperties, NULL, NULL);

        if (frameProperties != NULL) {
            CFRelease(frameProperties);
        }
        CVPixelBufferRelease(pixelBuffer);

        if (status != noErr) {
            RDVideoSetError(error, error_size, "The encoder refused the frame.");
            result = RDVideoStatusForError(status);
            return result;
        }

        // Wait for this picture. The callback signals on failure too, so a failed encode surfaces here rather
        // than as a silent timeout.
        [encoder waitForPacketWithTimeoutMs:effectiveTimeout];

        // Take the failure before the packets: if the encoder failed, what is queued belongs to an earlier frame
        // and is better left for the next call than reported alongside an exception.
        const OSStatus recorded = [encoder takeLastStatus];
        if (recorded != noErr) {
            RDVideoSetError(error, error_size, "The encoder failed while compressing.");
            result = RDVideoStatusForError(recorded);
            return result;
        }

        const int32_t extra = [encoder drainInto:output_packets + taken capacity:output_capacity - taken];
        *count_out += extra;
    }

    return result;
}

void rd_video_encoder_destroy(rd_video_encoder_ref encoder_ref)
{
    if (encoder_ref == NULL) {
        return;
    }

    @autoreleasepool {
        RDVideoEncoder *encoder = (__bridge_transfer RDVideoEncoder *)encoder_ref;
        if (encoder.session != NULL) {
            // After invalidate VideoToolbox will not call back again, so the queue cannot grow underneath the
            // release that follows.
            VTCompressionSessionInvalidate(encoder.session);
            CFRelease(encoder.session);
            encoder.session = NULL;
        }

        [encoder releaseQueue];
    }
}
