#import "rd_video_common.h"

#import <dispatch/dispatch.h>
#import <os/lock.h>
#import <stdlib.h>
#import <string.h>

#define RD_VIDEO_DEFAULT_TIMEOUT_MS 1000

// Declared before its definition because the session-creation method below needs it while the definition sits after
// the state machine in this file.
static void RDVideoDecoderOutput(void *refcon, void *sourceFrameRefcon, OSStatus status, VTDecodeInfoFlags infoFlags,
                                 CVImageBufferRef imageBuffer, CMTime presentationTimeStamp, CMTime presentationDuration);

#pragma mark - Decoder

/// A VideoToolbox decompression session, plus the picture that most recently became ready.
///
/// The session is created lazily, on the first access unit that carries parameter sets: the constructor takes a
/// format description, and a stream that has not begun has none. That is also what makes the decoder usable on a
/// stream whose dimensions are unknown in advance — the first picture tells us.
@interface RDVideoDecoder : NSObject

@property (nonatomic, assign) VTDecompressionSessionRef session;
@property (nonatomic, assign) rd_video_decoder_callback callback;
@property (nonatomic, assign) void *user;

/// The decoded picture, copied out of VideoToolbox's buffer into memory this layer owns.
@property (nonatomic, assign) uint8_t *y;
@property (nonatomic, assign) uint8_t *uv;
@property (nonatomic, assign) size_t yCapacity;
@property (nonatomic, assign) size_t uvCapacity;
@property (nonatomic, assign) int32_t width;
@property (nonatomic, assign) int32_t height;

/// Guards the picture and its buffers: the output callback runs on VideoToolbox's thread while the entry point
/// reads from the caller's.
@property (nonatomic, assign) os_unfair_lock lock;

@property (nonatomic, assign) BOOL ready;
@property (nonatomic, strong) dispatch_semaphore_t signal;

/// The format description the session was built from, kept because every CMSampleBuffer has to reference it and a
/// stream that changes resolution replaces it.
@property (nonatomic, assign) CMVideoFormatDescriptionRef sessionFormat;

/// A failure VideoToolbox reported through the callback.
@property (nonatomic, assign) OSStatus lastStatus;

@end

@implementation RDVideoDecoder

/// Publishes a picture. Called from VideoToolbox's callback thread.
- (void)publishY:(const uint8_t *)y uv:(const uint8_t *)uv yStride:(int32_t)yStride uvStride:(int32_t)uvStride
          width:(int32_t)width height:(int32_t)height
{
    // The picture is copied here rather than pointed at: VideoToolbox's buffer is only valid for the callback,
    // and the caller reads it after this returns.
    const size_t yBytes = (size_t)yStride * height;
    const size_t uvBytes = (size_t)uvStride * (height / 2);

    os_unfair_lock_lock(&self->_lock);

    if (self.yCapacity < yBytes) {
        free(self.y);
        self.y = malloc(yBytes);
        self.yCapacity = self.y == NULL ? 0 : yBytes;
    }
    if (self.uvCapacity < uvBytes) {
        free(self.uv);
        self.uv = malloc(uvBytes);
        self.uvCapacity = self.uv == NULL ? 0 : uvBytes;
    }

    if (self.y != NULL && self.uv != NULL) {
        // Row by row, because the destination is tightly packed and the source is not.
        for (int32_t row = 0; row < height; row++) {
            memcpy(self.y + ((size_t)row * width), y + ((size_t)row * yStride), (size_t)width);
        }
        for (int32_t row = 0; row < height / 2; row++) {
            memcpy(self.uv + ((size_t)row * width), uv + ((size_t)row * uvStride), (size_t)width);
        }
        self.width = width;
        self.height = height;
        self.ready = YES;
    }

    os_unfair_lock_unlock(&self->_lock);
    dispatch_semaphore_signal(self.signal);
}

/// Takes the published picture, transferring its bytes to the caller's description.
- (BOOL)takePictureInto:(rd_video_picture *)picture
{
    os_unfair_lock_lock(&self->_lock);

    if (!self.ready) {
        os_unfair_lock_unlock(&self->_lock);
        return NO;
    }

    self.ready = NO;
    picture->y = self.y;
    picture->uv = self.uv;
    picture->y_stride = self.width;
    picture->uv_stride = self.width;
    picture->width = self.width;
    picture->height = self.height;
    picture->has_frame = 1;

    os_unfair_lock_unlock(&self->_lock);
    return YES;
}

/// Waits for a picture to be published. `YES` means one is available.
- (BOOL)waitForPictureWithTimeoutMs:(int32_t)timeoutMs
{
    const dispatch_time_t deadline = dispatch_time(DISPATCH_TIME_NOW, (int64_t)timeoutMs * NSEC_PER_MSEC);
    for (;;) {
        os_unfair_lock_lock(&self->_lock);
        const BOOL ready = self.ready;
        os_unfair_lock_unlock(&self->_lock);

        if (ready) {
            return YES;
        }

        if (dispatch_semaphore_wait(self.signal, deadline) != 0) {
            return NO;
        }
    }
}

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

- (OSStatus)takeLastStatus
{
    os_unfair_lock_lock(&self->_lock);
    const OSStatus status = self.lastStatus;
    self.lastStatus = noErr;
    os_unfair_lock_unlock(&self->_lock);
    return status;
}

- (void)releaseBuffers
{
    os_unfair_lock_lock(&self->_lock);
    free(self.y);
    free(self.uv);
    self.y = NULL;
    self.uv = NULL;
    self.yCapacity = 0;
    self.uvCapacity = 0;
    os_unfair_lock_unlock(&self->_lock);
}

/// Creates or rebuilds the session from parameter sets.
///
/// VideoToolbox takes a format description rather than raw parameter sets, so the SPS and PPS become one through
/// CMVideoFormatDescriptionCreateFromH264ParameterSets — which is also the moment the stream's dimensions become
/// known, because it parses them out of the SPS.
- (int)ensureSessionWithSps:(const uint8_t *)sps spsSize:(size_t)spsSize pps:(const uint8_t *)pps ppsSize:(size_t)ppsSize
{
    const uint8_t *parameterSets[2] = {sps, pps};
    size_t parameterSetSizes[2] = {spsSize, ppsSize};

    CMFormatDescriptionRef description = NULL;
    const OSStatus status = CMVideoFormatDescriptionCreateFromH264ParameterSets(
        NULL, 2, parameterSets, parameterSetSizes, 4, &description);

    if (status != noErr || description == NULL) {
        return RD_ERROR_CODEC;
    }

    // Already running on exactly these parameter sets: nothing to rebuild.
    if (self.session != NULL && self.sessionFormat != NULL
        && CMFormatDescriptionEqual(self.sessionFormat, description)) {
        CFRelease(description);
        return RD_OK;
    }

    // NV12 out, because that is what the rest of the pipeline carries; asking for anything else would insert a
    // conversion here that the port contract says does not belong. Only the pixel format is specified: the backing
    // store is VideoToolbox's business, and naming an IOSurface configuration it does not want would fail the
    // session for no gain.
    const void *keys[1] = {kCVPixelBufferPixelFormatTypeKey};
    const void *values[1];
    int32_t pixelFormat = kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange;
    CFNumberRef formatNumber = CFNumberCreate(NULL, kCFNumberSInt32Type, &pixelFormat);
    CFDictionaryRef attributes = NULL;

    if (formatNumber == NULL) {
        CFRelease(description);
        return RD_ERROR_UNAVAILABLE;
    }

    values[0] = formatNumber;
    attributes = CFDictionaryCreate(
        NULL, keys, values, 1, &kCFCopyStringDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
    CFRelease(formatNumber);

    if (attributes == NULL) {
        CFRelease(description);
        return RD_ERROR_UNAVAILABLE;
    }

    // The callback is a record rather than two arguments: the function pointer and the refcon are one struct,
    // because a decoder can be given several of them.
    VTDecompressionOutputCallbackRecord callbackRecord = {RDVideoDecoderOutput, (__bridge void *)self};

    VTDecompressionSessionRef session = NULL;
    const OSStatus created = VTDecompressionSessionCreate(
        NULL, description, NULL, attributes, &callbackRecord, &session);

    CFRelease(attributes);

    if (created != noErr || session == NULL) {
        CFRelease(description);
        return RDVideoStatusForError(created);
    }

    if (self.session != NULL) {
        VTDecompressionSessionInvalidate(self.session);
        CFRelease(self.session);
    }
    if (self.sessionFormat != NULL) {
        CFRelease(self.sessionFormat);
    }

    self.session = session;
    self.sessionFormat = description;
    return RD_OK;
}

#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wobjc-missing-super-calls"
- (void)dealloc
{
    [self releaseBuffers];
}
#pragma clang diagnostic pop

@end

#pragma mark - Callback

/// VideoToolbox hands us a decoded picture, on a thread of its own.
static void RDVideoDecoderOutput(void *refcon, void *sourceFrameRefcon, OSStatus status, VTDecodeInfoFlags infoFlags,
                                 CVImageBufferRef imageBuffer, CMTime presentationTimeStamp, CMTime presentationDuration)
{
    (void)sourceFrameRefcon;
    (void)infoFlags;
    (void)presentationTimeStamp;
    (void)presentationDuration;

    RDVideoDecoder *decoder = (__bridge RDVideoDecoder *)refcon;
    if (decoder == nil) {
        return;
    }

    if (status != noErr || imageBuffer == NULL) {
        [decoder recordStatus:status == noErr ? -1 : status];
        return;
    }

    const OSType format = CVPixelBufferGetPixelFormatType(imageBuffer);
    if (format != kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange && format != kCVPixelFormatType_420YpCbCr8BiPlanarFullRange) {
        // Anything else would need a conversion this layer does not do, and handing back mislabelled planes is
        // worse than handing back nothing.
        [decoder recordStatus:-1];
        return;
    }

    if (CVPixelBufferLockBaseAddress(imageBuffer, kCVPixelBufferLock_ReadOnly) != kCVReturnSuccess) {
        [decoder recordStatus:-1];
        return;
    }

    const int32_t width = (int32_t)CVPixelBufferGetWidth(imageBuffer);
    const int32_t height = (int32_t)CVPixelBufferGetHeight(imageBuffer);

    uint8_t *y = (uint8_t *)CVPixelBufferGetBaseAddressOfPlane(imageBuffer, 0);
    uint8_t *uv = (uint8_t *)CVPixelBufferGetBaseAddressOfPlane(imageBuffer, 1);
    const size_t yStride = CVPixelBufferGetBytesPerRowOfPlane(imageBuffer, 0);
    const size_t uvStride = CVPixelBufferGetBytesPerRowOfPlane(imageBuffer, 1);

    if (y != NULL && uv != NULL && width > 0 && height > 0) {
        [decoder publishY:y
                        uv:uv
                   yStride:(int32_t)yStride
                   uvStride:(int32_t)uvStride
                     width:width
                    height:height];
    } else {
        [decoder recordStatus:-1];
    }

    CVPixelBufferUnlockBaseAddress(imageBuffer, kCVPixelBufferLock_ReadOnly);
}

/// Adds the format description as a property-backed ivar-free accessor pair, so the entry points can read it.
/// Declared here rather than in the @interface because it is the one piece of state the entry points touch but the
/// session class never changes on its own.

#pragma mark - Entry points

int rd_video_decoder_create(int32_t codec, rd_video_decoder_callback callback, void *user,
                            rd_video_decoder_ref *out, char *error, size_t error_size)
{
    if (out == NULL || callback == NULL) {
        RDVideoSetError(error, error_size, "The decoder needs an output handle and a callback.");
        return RD_ERROR_ARGUMENT;
    }

    *out = NULL;

    @autoreleasepool {
        RDVideoDecoder *decoder = [[RDVideoDecoder alloc] init];
        decoder.callback = callback;
        decoder.user = user;
        decoder.signal = dispatch_semaphore_create(0);

        (void)codec;   // Only H.264 today; the parameter exists so adding one is a decision, not a rewrite.

        *out = (__bridge_retained void *)decoder;
    }

    return RD_OK;
}

int rd_video_decoder_decode(rd_video_decoder_ref decoder_ref, const uint8_t *annex_b, size_t annex_b_size,
                            int64_t timestamp_ns, int32_t timeout_ms, rd_video_picture *picture, char *error, size_t error_size)
{
    if (decoder_ref == NULL || annex_b == NULL || picture == NULL) {
        RDVideoSetError(error, error_size, "Decoding needs a decoder, a bitstream, and an output description.");
        return RD_ERROR_ARGUMENT;
    }

    picture->y = NULL;
    picture->uv = NULL;
    picture->y_stride = 0;
    picture->uv_stride = 0;
    picture->width = 0;
    picture->height = 0;
    picture->has_frame = 0;

    const int32_t effectiveTimeout = timeout_ms > 0 ? timeout_ms : RD_VIDEO_DEFAULT_TIMEOUT_MS;
    (void)timestamp_ns;

    int result = RD_OK;

    @autoreleasepool {
        RDVideoDecoder *decoder = (__bridge RDVideoDecoder *)decoder_ref;

        // Split the access unit into NALs. Parameter sets are separated from the picture, because they go into the
        // format description while the picture goes into the sample buffer — they are not the same wire content.
        const uint8_t *nals[64];
        size_t nalSizes[64];
        const size_t nalCount = RDVideoAnnexBToAvcc(annex_b, annex_b_size, nals, nalSizes, 64);
        for (size_t i = 0; i < nalCount && i < 8; i++) {
        }
        if (nalCount == 0) {
            RDVideoSetError(error, error_size, "The access unit holds no NAL units.");
            result = RD_ERROR_CODEC;
            return result;
        }

        const uint8_t *sps = NULL, *pps = NULL;
        size_t spsSize = 0, ppsSize = 0;
        size_t pictureBytes = 0;
        for (size_t i = 0; i < nalCount; i++) {
            if (nalSizes[i] == 0) {
                continue;
            }

            const int type = nals[i][0] & 0x1F;
            if (type == RD_H264_NAL_SPS && sps == NULL) {
                sps = nals[i];
                spsSize = nalSizes[i];
            } else if (type == RD_H264_NAL_PPS && pps == NULL) {
                pps = nals[i];
                ppsSize = nalSizes[i];
            } else {
                pictureBytes += 4 + nalSizes[i];
            }
        }

        // Parameter sets are what create or resize the session. Mid-stream ones that differ from what the session
        // was built with are a stream change, which VideoToolbox will not absorb silently — the session is rebuilt
        // and the next picture comes back at the new size.
        if (sps != NULL && pps != NULL) {
            const int status = [decoder ensureSessionWithSps:sps spsSize:spsSize pps:pps ppsSize:ppsSize];
            if (status != RD_OK) {
                RDVideoSetError(error, error_size, "The parameter sets could not be turned into a format description.");
                result = RD_ERROR_CODEC;
                return result;
            }
        } else if (decoder.sessionFormat == NULL) {
            RDVideoSetError(error, error_size, "No key frame has arrived yet, so the decoder cannot start.");
            result = RD_ERROR_CODEC;
            return result;
        }

        if (pictureBytes == 0) {
            // Parameter sets alone: nothing to decode, and not an error — streams do send them that way.
            return RD_OK;
        }

        // Repack into AVCC, which is what a CMSampleBuffer wants: a four-byte big-endian length before each NAL.
        uint8_t *avcc = malloc(pictureBytes);
        if (avcc == NULL) {
            RDVideoSetError(error, error_size, "Out of memory repacking the access unit.");
            result = RD_ERROR_UNAVAILABLE;
            return result;
        }

        size_t written = 0;
        for (size_t i = 0; i < nalCount; i++) {
            if (nalSizes[i] == 0) {
                continue;
            }

            const int type = nals[i][0] & 0x1F;
            if (type == RD_H264_NAL_SPS || type == RD_H264_NAL_PPS) {
                continue;
            }

            const size_t length = nalSizes[i];
            avcc[written++] = (uint8_t)(length >> 24);
            avcc[written++] = (uint8_t)(length >> 16);
            avcc[written++] = (uint8_t)(length >> 8);
            avcc[written++] = (uint8_t)length;
            memcpy(avcc + written, nals[i], length);
            written += length;
        }

        // NULL for the block allocator means the block buffer takes ownership of this memory and releases it with
        // the default allocator when the sample buffer is done with it. There is deliberately no free() after
        // this: doing both is a double free, and the memory has to outlive this function anyway because the
        // session may still hold the sample buffer when it returns.
        CMBlockBufferRef block = NULL;
        OSStatus status = CMBlockBufferCreateWithMemoryBlock(
            NULL, avcc, written, NULL, NULL, 0, written, 0, &block);

        if (status != noErr || block == NULL) {
            // The buffer never took it, so this is the one path that still has to release the memory by hand.
            free(avcc);
            RDVideoSetError(error, error_size, "The access unit could not be wrapped for VideoToolbox.");
            result = RDVideoStatusForError(status);
            return result;
        }

        if (status != noErr || block == NULL) {
            RDVideoSetError(error, error_size, "The access unit could not be wrapped for VideoToolbox.");
            result = RDVideoStatusForError(status);
            return result;
        }

        // One sample per access unit. The timing and size tables are both empty and both NULL: passing a non-zero
        // entry count with a NULL array makes CoreMedia copy from NULL, which is a crash inside memmove rather
        // than a returned error.
        CMSampleBufferRef sample = NULL;
        status = CMSampleBufferCreate(
            NULL, block, true, NULL, NULL, decoder.sessionFormat, 1, 0, NULL, 0, NULL, &sample);

        if (status != noErr || sample == NULL) {
            CFRelease(block);
            RDVideoSetError(error, error_size, "The access unit could not be built into a sample buffer.");
            result = RDVideoStatusForError(status);
            return result;
        }

        status = VTDecompressionSessionDecodeFrame(decoder.session, sample, 0, NULL, NULL);
        CFRelease(sample);
        CFRelease(block);

        if (status != noErr) {
            RDVideoSetError(error, error_size, "VideoToolbox refused the access unit.");
            result = RDVideoStatusForError(status);
            return result;
        }

        // The callback signals on failure too, so a failed decode surfaces here rather than as a silent timeout.
        [decoder waitForPictureWithTimeoutMs:effectiveTimeout];

        const OSStatus recorded = [decoder takeLastStatus];
        if (recorded != noErr) {
            RDVideoSetError(error, error_size, "VideoToolbox failed while decoding.");
            result = RDVideoStatusForError(recorded);
            return result;
        }

        // No picture is not a failure: the access unit may have been parameter sets alone, or the decoder may still
        // be filling its pipeline. The caller retries with the next one.
        [decoder takePictureInto:picture];
    }

    return result;
}

void rd_video_decoder_destroy(rd_video_decoder_ref decoder_ref)
{
    if (decoder_ref == NULL) {
        return;
    }

    @autoreleasepool {
        RDVideoDecoder *decoder = (__bridge_transfer RDVideoDecoder *)decoder_ref;
        if (decoder.session != NULL) {
            VTDecompressionSessionInvalidate(decoder.session);
            CFRelease(decoder.session);
            decoder.session = NULL;
        }

        [decoder releaseBuffers];
    }
}
