//
//  rd_screen_capture.m
//
//  ScreenCaptureKit behind the C ABI in rd_native.h.
//
//  The awkward parts, and how they are handled here:
//
//    * Shareable content is asynchronous. rd_capture_create blocks on it with a semaphore, because the alternative
//      is leaking a two-phase construction up into the managed layer for no benefit. It must therefore not be
//      called on the main thread; the managed wrapper runs it on the thread pool.
//    * Frames arrive through an SCStreamOutput delegate on a queue we supply. That queue is serial, so the callback
//      never runs concurrently with itself and the callee needs no locking of its own.
//    * The CVPixelBuffer is only valid inside the delegate call. Nothing is retained across it: the pixels are
//      memcpy'd by the callee while the buffer is locked, which is the only correct time to read them.
//    * ScreenCaptureKit never reports the pointer's shape, only whether to composite it. The pointer is therefore
//      excluded from the image (showsCursor = NO) and read separately, matching what the Windows capturer does.
//

#import "rd_native.h"

#import <CoreMedia/CoreMedia.h>
#import <CoreVideo/CoreVideo.h>
#import <Foundation/Foundation.h>
#import <ScreenCaptureKit/ScreenCaptureKit.h>

/// Fills the caller's message buffer, tolerating a null or zero-length buffer.
static void RDSetError(char *error, size_t errorSize, NSString *message)
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
static int RDStatusForError(NSError *error)
{
    if (error == nil) {
        return RD_OK;
    }

    // SCStreamErrorUserDeclined and the capture-source failures all mean the same thing to the caller: the user has
    // not granted Screen Recording, so the fix is a permission prompt rather than a retry.
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

#pragma mark - Frame delivery

/// Receives the stream's frames and forwards the ones that carry new content.
@interface RDStreamOutput : NSObject <SCStreamOutput, SCStreamDelegate>
@property (nonatomic, assign) rd_frame_callback callback;
@property (nonatomic, assign) void *user;
/// Set by the delegate when ScreenCaptureKit stops the stream on its own, so the managed side can tell a failure
/// apart from a normal stop.
@property (nonatomic, assign) BOOL stoppedUnexpectedly;
@property (nonatomic, copy) NSString *stopReason;
@end

@implementation RDStreamOutput

- (void)stream:(SCStream *)stream didOutputSampleBuffer:(CMSampleBufferRef)sampleBuffer ofType:(SCStreamOutputType)type
{
    // Audio shares this delegate on macOS 13+, and there is nothing to do with it here.
    if (type != SCStreamOutputTypeScreen || self.callback == NULL) {
        return;
    }

    SCFrameStatus status = SCFrameStatusComplete;
    CFArrayRef attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, false);
    if (attachments != NULL && CFArrayGetCount(attachments) > 0) {
        CFDictionaryRef attachment = (CFDictionaryRef)CFArrayGetValueAtIndex(attachments, 0);
        CFNumberRef statusValue = (CFNumberRef)CFDictionaryGetValue(
            attachment, (__bridge CFStringRef)SCStreamFrameInfoStatus);
        if (statusValue != NULL) {
            NSInteger raw = SCFrameStatusComplete;
            CFNumberGetValue(statusValue, kCFNumberNSIntegerType, &raw);
            status = (SCFrameStatus)raw;
        }
    }

    // Idle, blank, suspended and stopped frames carry no new pixels. Dropping them here is what gives the managed
    // side its "nothing changed" signal: it simply never hears from us, and its wait runs out.
    if (status != SCFrameStatusComplete && status != SCFrameStatusStarted) {
        return;
    }

    CVPixelBufferRef pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer);
    if (pixelBuffer == NULL) {
        return;
    }

    // Read-only is enough and avoids a copy-on-write of the surface.
    if (CVPixelBufferLockBaseAddress(pixelBuffer, kCVPixelBufferLock_ReadOnly) != kCVReturnSuccess) {
        return;
    }

    @try {
        uint8_t *base = (uint8_t *)CVPixelBufferGetBaseAddress(pixelBuffer);
        const int32_t width = (int32_t)CVPixelBufferGetWidth(pixelBuffer);
        const int32_t height = (int32_t)CVPixelBufferGetHeight(pixelBuffer);
        const int32_t bytesPerRow = (int32_t)CVPixelBufferGetBytesPerRow(pixelBuffer);

        // The dirty rectangles are only a hint, and ScreenCaptureKit omits them for the first frame. Reporting
        // none is defined by the contract as "the whole frame changed", which is the safe reading.
        rd_rect dirty[16];
        int32_t dirtyCount = 0;
        CFArrayRef dirtyRects = NULL;
        if (attachments != NULL && CFArrayGetCount(attachments) > 0) {
            CFDictionaryRef attachment = (CFDictionaryRef)CFArrayGetValueAtIndex(attachments, 0);
            dirtyRects = (CFArrayRef)CFDictionaryGetValue(
                attachment, (__bridge CFStringRef)SCStreamFrameInfoDirtyRects);
        }

        if (dirtyRects != NULL) {
            CFIndex count = CFArrayGetCount(dirtyRects);
            for (CFIndex i = 0; i < count && dirtyCount < (int32_t)(sizeof(dirty) / sizeof(dirty[0])); i++) {
                CGRect rect;
                if (CGRectMakeWithDictionaryRepresentation(
                        (CFDictionaryRef)CFArrayGetValueAtIndex(dirtyRects, i), &rect)) {
                    dirty[dirtyCount].x = (int32_t)floor(rect.origin.x);
                    dirty[dirtyCount].y = (int32_t)floor(rect.origin.y);
                    dirty[dirtyCount].width = (int32_t)ceil(rect.size.width);
                    dirty[dirtyCount].height = (int32_t)ceil(rect.size.height);
                    dirtyCount++;
                }
            }
        }

        // The sample's presentation stamp is on the stream's clock, which for a display stream is host time:
        // monotonic and unaffected by wall-clock changes, which is what a frame timestamp should be.
        CMTime presentation = CMSampleBufferGetPresentationTimeStamp(sampleBuffer);
        uint64_t hostTimeNs = 0;
        if (CMTIME_IS_VALID(presentation)) {
            hostTimeNs = (uint64_t)(CMTimeGetSeconds(presentation) * 1e9);
        }

        rd_frame frame = {
            .bgra = base,
            .width = width,
            .height = height,
            .bytes_per_row = bytesPerRow,
            .host_time_ns = hostTimeNs,
            .dirty_rects = dirtyCount > 0 ? dirty : NULL,
            .dirty_count = dirtyCount,
        };

        self.callback(self.user, &frame);
    } @finally {
        CVPixelBufferUnlockBaseAddress(pixelBuffer, kCVPixelBufferLock_ReadOnly);
    }
}

- (void)stream:(SCStream *)stream didStopWithError:(NSError *)error
{
    self.stoppedUnexpectedly = YES;
    self.stopReason = error.localizedDescription ?: @"The stream stopped.";
}

@end

#pragma mark - Capture

/// One running (or ready) capture. Held by the C handle through a retained bridge, so ARC owns its lifetime.
@interface RDCapture : NSObject
@property (nonatomic, strong) SCContentFilter *filter;
@property (nonatomic, strong) SCStreamConfiguration *configuration;
@property (nonatomic, strong) SCStream *stream;
@property (nonatomic, strong) RDStreamOutput *output;
@property (nonatomic, strong) dispatch_queue_t queue;
@property (nonatomic, assign) BOOL started;
@end

@implementation RDCapture
@end

/// Looks up one display in the shareable content, or nil.
static SCDisplay *RDFindDisplay(SCShareableContent *content, uint32_t displayId)
{
    for (SCDisplay *display in content.displays) {
        if (display.displayID == displayId) {
            return display;
        }
    }

    return nil;
}

int rd_capture_create(uint32_t display_id,
                      int32_t pixel_width,
                      int32_t pixel_height,
                      int32_t frames_per_second,
                      rd_frame_callback callback,
                      void *user,
                      rd_capture_ref *out,
                      char *error,
                      size_t error_size)
{
    if (out == NULL || callback == NULL) {
        RDSetError(error, error_size, @"rd_capture_create needs an output handle and a callback.");
        return RD_ERROR_ARGUMENT;
    }

    *out = NULL;

    if (pixel_width <= 0 || pixel_height <= 0) {
        RDSetError(error, error_size, @"The requested capture size must be positive.");
        return RD_ERROR_ARGUMENT;
    }

    @autoreleasepool {
        __block SCShareableContent *content = nil;
        __block NSError *lookupError = nil;

        // Blocking here is deliberate; see the note at the top of the file. The timeout is a safety net for a
        // ScreenCaptureKit that never answers, which would otherwise hang the caller forever.
        dispatch_semaphore_t ready = dispatch_semaphore_create(0);
        [SCShareableContent getShareableContentWithCompletionHandler:^(SCShareableContent *result, NSError *err) {
            content = result;
            lookupError = err;
            dispatch_semaphore_signal(ready);
        }];

        if (dispatch_semaphore_wait(ready, dispatch_time(DISPATCH_TIME_NOW, (int64_t)(10 * NSEC_PER_SEC))) != 0) {
            RDSetError(error, error_size, @"ScreenCaptureKit did not answer the shareable-content request in time.");
            return RD_ERROR_TIMEOUT;
        }

        if (content == nil) {
            RDSetError(error, error_size, lookupError.localizedDescription ?: @"No shareable content is available.");
            return RDStatusForError(lookupError);
        }

        SCDisplay *display = RDFindDisplay(content, display_id);
        if (display == nil) {
            RDSetError(error, error_size, [NSString stringWithFormat:@"Display %u is not shareable.", display_id]);
            return RD_ERROR_DISPLAY_NOT_FOUND;
        }

        RDCapture *capture = [[RDCapture alloc] init];

        // Excluding no windows means "the whole display, composited" — which is what a remote desktop wants.
        capture.filter = [[SCContentFilter alloc] initWithDisplay:display excludingWindows:@[]];

        SCStreamConfiguration *configuration = [[SCStreamConfiguration alloc] init];
        configuration.width = (size_t)pixel_width;
        configuration.height = (size_t)pixel_height;
        configuration.pixelFormat = kCVPixelFormatType_32BGRA;
        configuration.minimumFrameInterval = CMTimeMake(1, frames_per_second > 0 ? frames_per_second : 60);
        // The pointer is reported separately so the controller can draw it; compositing it here would make the
        // remote pointer lag the local one and would double-draw on the controller.
        configuration.showsCursor = NO;
        // A shallow queue keeps latency down; frames that arrive while the managed side is busy are dropped by
        // ScreenCaptureKit rather than queued, which is the behaviour a live view wants.
        configuration.queueDepth = 3;
        // Frames come back at the display's backing size; asking it not to rescale keeps pixels exact.
        configuration.scalesToFit = NO;
        capture.configuration = configuration;

        RDStreamOutput *output = [[RDStreamOutput alloc] init];
        output.callback = callback;
        output.user = user;
        capture.output = output;

        // A serial queue is what makes the callback single-threaded from the callee's point of view.
        capture.queue = dispatch_queue_create("com.remotedesktop.capture", DISPATCH_QUEUE_SERIAL);

        capture.stream = [[SCStream alloc] initWithFilter:capture.filter
                                            configuration:configuration
                                                 delegate:output];

        NSError *addError = nil;
        if (![capture.stream addStreamOutput:output
                                        type:SCStreamOutputTypeScreen
                          sampleHandlerQueue:capture.queue
                                       error:&addError]) {
            RDSetError(error, error_size, addError.localizedDescription ?: @"The stream rejected the output.");
            return RDStatusForError(addError);
        }

        *out = (__bridge_retained void *)capture;
        return RD_OK;
    }
}

int rd_capture_start(rd_capture_ref handle, char *error, size_t error_size)
{
    RDCapture *capture = (__bridge RDCapture *)handle;
    if (capture == nil) {
        RDSetError(error, error_size, @"The capture handle is not valid.");
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
            RDSetError(error, error_size, @"The stream did not start in time.");
            return RD_ERROR_TIMEOUT;
        }

        if (startError != nil) {
            RDSetError(error, error_size, startError.localizedDescription ?: @"The stream failed to start.");
            return RDStatusForError(startError);
        }

        capture.started = YES;
        return RD_OK;
    }
}

void rd_capture_stop(rd_capture_ref handle)
{
    RDCapture *capture = (__bridge RDCapture *)handle;
    if (capture == nil || !capture.started) {
        return;
    }

    capture.started = NO;

    // Stopping is asynchronous, but a caller that asked to stop needs it stopped when this returns: the managed
    // side is about to dispose the buffers the callback writes into.
    dispatch_semaphore_t stopped = dispatch_semaphore_create(0);
    [capture.stream stopCaptureWithCompletionHandler:^(NSError *err) {
        (void)err;
        dispatch_semaphore_signal(stopped);
    }];
    dispatch_semaphore_wait(stopped, dispatch_time(DISPATCH_TIME_NOW, (int64_t)(5 * NSEC_PER_SEC)));
}

void rd_capture_destroy(rd_capture_ref handle)
{
    if (handle == NULL) {
        return;
    }

    // Take ownership back from the bridge; ARC releases it at the end of this scope.
    RDCapture *capture = (__bridge_transfer RDCapture *)handle;
    rd_capture_stop((__bridge void *)capture);

    // Clearing the callback before the stream is released closes the window where a frame in flight could still
    // reach managed memory that is being torn down.
    capture.output.callback = NULL;
    capture.output.user = NULL;
    capture.stream = nil;
    capture.output = nil;
    capture.filter = nil;
    capture.configuration = nil;
}
