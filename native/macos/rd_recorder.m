//
//  rd_recorder.m
//
//  Video recording — AVAssetWriter behind the C ABI in rd_native.h.
//
//  Why AVAssetWriter rather than pulling the P4 VideoToolbox encoder and muxing the H.264 ourselves: muxing an MP4
//  container by hand (moov atom, sample tables, parameter sets) is hundreds of lines of brittle bit-twiddling, and
//  it is exactly the part a codec test cannot catch. AVAssetWriter already does H.264 compression through VideoToolbox
//  and the MP4 muxing in one call, so the recorder hands it raw BGRA pixel buffers and asks for an .mp4 back. That
//  keeps the recorder to the one job it adds over the live pipeline: a timeline and a file.
//
//  The frames come in top-row-first, and CVPixelBuffer memory is also top-row-first, so they go in without the flip
//  Media Foundation needs (its RGB32 is bottom-up DIB). The orientation probe below exists to prove that assumption,
//  not because it is in doubt — a solid-colour frame would pass a size check and still be upside down.
//

#import "rd_native.h"

#import <AVFoundation/AVFoundation.h>
#import <CoreMedia/CoreMedia.h>
#import <CoreVideo/CoreVideo.h>
#import <Foundation/Foundation.h>
#import <ServiceManagement/ServiceManagement.h>

/// Fills the caller's message buffer, tolerating a null or zero-length buffer.
static void RDRecorderSetError(char *error, size_t errorSize, NSString *message)
{
    if (error == NULL || errorSize == 0) {
        return;
    }

    const char *utf8 = message.UTF8String ?: "recorder error";
    strncpy(error, utf8, errorSize - 1);
    error[errorSize - 1] = '\0';
}

/// One open recording. Held by the C handle through a retained bridge so ARC owns its lifetime.
@interface RDMovieRecorder : NSObject
@property (nonatomic, strong) AVAssetWriter *writer;
@property (nonatomic, strong) AVAssetWriterInput *input;
@property (nonatomic, assign) int32_t width;
@property (nonatomic, assign) int32_t height;
@property (nonatomic, assign) int32_t frameRate;
/// Frames accepted so far. Drives the "nothing recorded" decision and the file's progress.
@property (nonatomic, assign) int64_t frameCount;
/// A frame failed to append. The file is still finalized (saving what went before) but the caller is told via this.
@property (nonatomic, assign) BOOL broken;
/// startWriting / finishWriting are one-shot; these guard against a second finalize.
@property (nonatomic, assign) BOOL started;
@property (nonatomic, assign) BOOL finished;
@end

@implementation RDMovieRecorder
@end

int rd_recorder_create(const char *path,
                       int32_t width,
                       int32_t height,
                       int32_t frame_rate,
                       int32_t bitrate_kbps,
                       rd_recorder_ref *out,
                       char *error,
                       size_t error_size)
{
    if (out == NULL) {
        RDRecorderSetError(error, error_size, @"rd_recorder_create needs an output handle.");
        return RD_ERROR_ARGUMENT;
    }

    *out = NULL;

    if (path == NULL || width <= 0 || height <= 0) {
        RDRecorderSetError(error, error_size, @"rd_recorder_create needs a path and positive dimensions.");
        return RD_ERROR_ARGUMENT;
    }

    @autoreleasepool {
        NSString *pathString = [NSString stringWithUTF8String:path];
        NSURL *url = [NSURL fileURLWithPath:pathString];
        NSError *writerError = nil;

        // MPEG-4, not QuickTime: the contract says MP4, and an .mp4 is what every player and the web expect.
        AVAssetWriter *writer = [[AVAssetWriter alloc] initWithURL:url fileType:AVFileTypeMPEG4 error:&writerError];
        if (writer == nil) {
            RDRecorderSetError(error, error_size, writerError.localizedDescription ?: @"AVAssetWriter could not be created.");
            return RD_ERROR_CODEC;
        }

        // Baseline rather than High on purpose: a screen recording carries no profile-dependent features worth the
        // compatibility cost, and Baseline plays in the widest set of players, including browsers.
        NSMutableDictionary *compression = [NSMutableDictionary dictionary];
        if (bitrate_kbps > 0) {
            [compression setObject:@(bitrate_kbps * 1000) forKey:AVVideoAverageBitRateKey];
        }
        [compression setObject:AVVideoProfileLevelH264BaselineAutoLevel forKey:AVVideoProfileLevelKey];
        // One picture out per picture in: no B-frames, so a truncated file still plays up to the cut and the decoder
        // never has to wait on a frame that will never arrive.
        [compression setObject:@NO forKey:AVVideoAllowFrameReorderingKey];

        NSDictionary *settings = @{
            AVVideoCodecKey: AVVideoCodecTypeH264,
            AVVideoWidthKey: @(width),
            AVVideoHeightKey: @(height),
            AVVideoCompressionPropertiesKey: compression,
        };

        AVAssetWriterInput *input = [[AVAssetWriterInput alloc] initWithMediaType:AVMediaTypeVideo
                                                                  outputSettings:settings];
        // The frames arrive from a worker thread, not from a live capture device, so the writer buffers for us instead
        // of demanding them in real time. This is what lets the managed side push at its own pace.
        input.expectsMediaDataInRealTime = NO;

        if (![writer canAddInput:input]) {
            RDRecorderSetError(error, error_size, @"AVAssetWriter refused the H.264 input.");
            return RD_ERROR_CODEC;
        }

        [writer addInput:input];

        if (![writer startWriting]) {
            RDRecorderSetError(error, error_size, writer.error.localizedDescription ?: @"AVAssetWriter failed to start.");
            return RD_ERROR_CODEC;
        }

        // The recording's clock starts at zero; every frame is timed from there by the index.
        [writer startSessionAtSourceTime:kCMTimeZero];

        RDMovieRecorder *recorder = [[RDMovieRecorder alloc] init];
        recorder.writer = writer;
        recorder.input = input;
        recorder.width = width;
        recorder.height = height;
        recorder.frameRate = MAX(1, frame_rate);
        recorder.frameCount = 0;
        recorder.broken = NO;
        recorder.started = YES;
        recorder.finished = NO;

        *out = (__bridge_retained void *)recorder;
        return RD_OK;
    }
}

int rd_recorder_append(rd_recorder_ref recorder,
                       const uint8_t *bgra,
                       size_t length,
                       int32_t stride,
                       int32_t width,
                       int32_t height,
                       char *error,
                       size_t error_size)
{
    RDMovieRecorder *r = (__bridge RDMovieRecorder *)recorder;
    if (r == nil) {
        RDRecorderSetError(error, error_size, @"The recorder handle is not valid.");
        return RD_ERROR_ARGUMENT;
    }

    // A frame that arrived after a failure already happened is ignored — the recording is over, only the flush is left.
    if (r.broken || r.finished) {
        return RD_OK;
    }

    if (bgra == NULL) {
        RDRecorderSetError(error, error_size, @"rd_recorder_append needs pixel data.");
        return RD_ERROR_ARGUMENT;
    }

    if (width != r.width || height != r.height) {
        RDRecorderSetError(error, error_size, @"A frame of a different size cannot enter a fixed-input recording.");
        r.broken = YES;
        return RD_ERROR_ARGUMENT;
    }

    if (stride < width * 4 || length < (size_t)((height - 1) * stride) + (size_t)(width * 4)) {
        RDRecorderSetError(error, error_size, @"The frame buffer is shorter than its dimensions claim.");
        r.broken = YES;
        return RD_ERROR_ARGUMENT;
    }

    @autoreleasepool {
        CVPixelBufferRef pixelBuffer = NULL;
        NSDictionary *attributes = @{
            (NSString *)kCVPixelBufferCGImageCompatibilityKey: @NO,
            (NSString *)kCVPixelBufferCGBitmapContextCompatibilityKey: @NO,
        };

        CVReturn cvStatus = CVPixelBufferCreate(kCFAllocatorDefault,
                                                (size_t)width,
                                                (size_t)height,
                                                kCVPixelFormatType_32BGRA,
                                                (__bridge CFDictionaryRef)attributes,
                                                &pixelBuffer);
        if (cvStatus != kCVReturnSuccess || pixelBuffer == NULL) {
            RDRecorderSetError(error, error_size, @"Could not allocate a pixel buffer for the frame.");
            r.broken = YES;
            return RD_ERROR_CODEC;
        }

        // Copy row by row: the source may be row-padded (stride > width * 4), the destination is tightly packed the
        // way CVPixelBuffer lays it out. Top row first on both sides, so no flip.
        CVPixelBufferLockBaseAddress(pixelBuffer, 0);
        uint8_t *base = CVPixelBufferGetBaseAddress(pixelBuffer);
        const size_t destinationRowBytes = CVPixelBufferGetBytesPerRow(pixelBuffer);
        const size_t rowBytes = (size_t)width * 4;
        for (int32_t y = 0; y < height; y++) {
            memcpy(base + (size_t)y * destinationRowBytes, bgra + (size_t)y * stride, rowBytes);
        }
        CVPixelBufferUnlockBaseAddress(pixelBuffer, 0);

        CMVideoFormatDescriptionRef format = NULL;
        OSStatus status = CMVideoFormatDescriptionCreateForImageBuffer(kCFAllocatorDefault, pixelBuffer, &format);
        if (status != noErr || format == NULL) {
            CVPixelBufferRelease(pixelBuffer);
            RDRecorderSetError(error, error_size, @"Could not describe the frame for the writer.");
            r.broken = YES;
            return RD_ERROR_CODEC;
        }

        // Frame index over the frame rate is the timeline; the duration is one frame.
        CMSampleTimingInfo timing = {
            .duration = CMTimeMake(1, r.frameRate),
            .presentationTimeStamp = CMTimeMake(r.frameCount, r.frameRate),
            .decodeTimeStamp = kCMTimeInvalid,
        };

        CMSampleBufferRef sampleBuffer = NULL;
        status = CMSampleBufferCreateReadyWithImageBuffer(kCFAllocatorDefault,
                                                          pixelBuffer,
                                                          format,
                                                          &timing,
                                                          &sampleBuffer);
        CFRelease(format);
        if (status != noErr || sampleBuffer == NULL) {
            CVPixelBufferRelease(pixelBuffer);
            RDRecorderSetError(error, error_size, @"Could not wrap the frame for the writer.");
            r.broken = YES;
            return RD_ERROR_CODEC;
        }

        // Backpressure: with expectsMediaDataInRealTime off, the input buffers and occasionally is not ready. Wait a
        // bounded amount rather than drop — a dropped frame here is a hole in the recording with no caller to notice.
        int budget = 0;
        while (!r.input.readyForMoreMediaData) {
            if (++budget > 600) { // ~3s at 5ms
                CFRelease(sampleBuffer);
                CVPixelBufferRelease(pixelBuffer);
                RDRecorderSetError(error, error_size, @"The writer stopped accepting frames.");
                r.broken = YES;
                return RD_ERROR_CODEC;
            }
            [NSThread sleepForTimeInterval:0.005];
        }

        BOOL appended = [r.input appendSampleBuffer:sampleBuffer];

        CFRelease(sampleBuffer);
        CVPixelBufferRelease(pixelBuffer);

        if (!appended) {
            RDRecorderSetError(error, error_size, @"The writer refused a frame.");
            r.broken = YES;
            return RD_ERROR_CODEC;
        }

        r.frameCount++;
        return RD_OK;
    }
}

int rd_recorder_finish(rd_recorder_ref recorder,
                       int64_t *out_wrote_frames,
                       char *error,
                       size_t error_size)
{
    RDMovieRecorder *r = (__bridge RDMovieRecorder *)recorder;
    if (out_wrote_frames != NULL) {
        *out_wrote_frames = 0;
    }

    if (r == nil) {
        RDRecorderSetError(error, error_size, @"The recorder handle is not valid.");
        return RD_ERROR_ARGUMENT;
    }

    if (r.finished) {
        if (out_wrote_frames != NULL) {
            *out_wrote_frames = r.frameCount;
        }
        return RD_OK;
    }

    r.finished = YES;

    // Nothing ever reached the writer: there is no recording, so cancel rather than finalize and take the empty file
    // startWriting may have created away. The caller treats the zero count as "nothing was recorded".
    if (r.frameCount == 0) {
        [r.writer cancelWriting];
        [[NSFileManager defaultManager] removeItemAtURL:r.writer.outputURL error:nil];
        return RD_OK;
    }

    [r.input markAsFinished];

    // A mid-recording failure still leaves every frame before it in the file, so finalize and report the count; the
    // caller already carries the WriteFailed reason. finishWriting is deprecated for the synchronous form, so the
    // completion-handler variant runs and a semaphore collapses it back to the synchronous result this ABI promises.
    __block BOOL finalized = NO;
    dispatch_semaphore_t gate = dispatch_semaphore_create(0);
    [r.writer finishWritingWithCompletionHandler:^{
        finalized = (r.writer.status == AVAssetWriterStatusCompleted);
        dispatch_semaphore_signal(gate);
    }];
    dispatch_semaphore_wait(gate, DISPATCH_TIME_FOREVER);

    if (!finalized) {
        NSError *failure = r.writer.error;
        RDRecorderSetError(error, error_size, failure.localizedDescription ?: @"The recording could not be finalized.");
        if (out_wrote_frames != NULL) {
            *out_wrote_frames = r.frameCount;
        }
        return RD_ERROR_CODEC;
    }

    if (out_wrote_frames != NULL) {
        *out_wrote_frames = r.frameCount;
    }
    return RD_OK;
}

void rd_recorder_destroy(rd_recorder_ref recorder)
{
    if (recorder == NULL) {
        return;
    }

    // Take ownership back from the bridge; ARC releases it at the end of this scope.
    RDMovieRecorder *r = (__bridge_transfer RDMovieRecorder *)recorder;

    // A destroy mid-recording should not leave a half-finalized file behind.
    if (!r.finished && r.writer.status == AVAssetWriterStatusWriting) {
        [r.writer cancelWriting];
    }

    r.writer = nil;
    r.input = nil;
}

int rd_recorder_probe_orientation(const char *path,
                                  double *out_top_luma,
                                  double *out_bottom_luma,
                                  char *error,
                                  size_t error_size)
{
    if (out_top_luma != NULL) {
        *out_top_luma = 0;
    }
    if (out_bottom_luma != NULL) {
        *out_bottom_luma = 0;
    }

    if (path == NULL) {
        RDRecorderSetError(error, error_size, @"rd_recorder_probe_orientation needs a path.");
        return RD_ERROR_ARGUMENT;
    }

    @autoreleasepool {
        NSString *pathString = [NSString stringWithUTF8String:path];
        NSURL *url = [NSURL fileURLWithPath:pathString];

        AVURLAsset *asset = [AVURLAsset URLAssetWithURL:url options:nil];
        NSError *readerError = nil;
        AVAssetReader *reader = [[AVAssetReader alloc] initWithAsset:asset error:&readerError];
        if (reader == nil) {
            RDRecorderSetError(error, error_size, readerError.localizedDescription ?: @"Could not open the recording.");
            return RD_ERROR_CODEC;
        }

        AVAssetTrack *track = [[asset tracksWithMediaType:AVMediaTypeVideo] firstObject];
        if (track == nil) {
            RDRecorderSetError(error, error_size, @"The recording has no video track.");
            return RD_ERROR_CODEC;
        }

        // BGRA out: luma is a cheap weighted sum of the channels, no colour conversion needed.
        NSDictionary *outputSettings = @{
            (NSString *)kCVPixelBufferPixelFormatTypeKey: @(kCVPixelFormatType_32BGRA),
        };
        AVAssetReaderTrackOutput *output =
            [[AVAssetReaderTrackOutput alloc] initWithTrack:track outputSettings:outputSettings];
        [reader addOutput:output];

        if (![reader startReading]) {
            RDRecorderSetError(error, error_size, reader.error.localizedDescription ?: @"Could not read the recording.");
            return RD_ERROR_CODEC;
        }

        CMSampleBufferRef sampleBuffer = NULL;
        while ((sampleBuffer = [output copyNextSampleBuffer]) != NULL) {
            CVPixelBufferRef pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer);
            if (pixelBuffer == NULL) {
                CFRelease(sampleBuffer);
                continue;
            }

            CVPixelBufferLockBaseAddress(pixelBuffer, kCVPixelBufferLock_ReadOnly);
            const uint8_t *base = CVPixelBufferGetBaseAddress(pixelBuffer);
            const size_t rowBytes = CVPixelBufferGetBytesPerRow(pixelBuffer);
            const int32_t h = (int32_t)CVPixelBufferGetHeight(pixelBuffer);
            const int32_t w = (int32_t)CVPixelBufferGetWidth(pixelBuffer);

            double top = 0;
            for (int32_t x = 0; x < w; x++) {
                const uint8_t *p = base + (size_t)x * 4;
                top += 0.299 * p[2] + 0.587 * p[1] + 0.114 * p[0];
            }
            top /= w;

            const uint8_t *last = base + (size_t)(h - 1) * rowBytes;
            double bottom = 0;
            for (int32_t x = 0; x < w; x++) {
                const uint8_t *p = last + (size_t)x * 4;
                bottom += 0.299 * p[2] + 0.587 * p[1] + 0.114 * p[0];
            }
            bottom /= w;

            CVPixelBufferUnlockBaseAddress(pixelBuffer, kCVPixelBufferLock_ReadOnly);
            CFRelease(sampleBuffer);

            if (out_top_luma != NULL) {
                *out_top_luma = top;
            }
            if (out_bottom_luma != NULL) {
                *out_bottom_luma = bottom;
            }
            break;
        }

        [reader cancelReading];
        return RD_OK;
    }
}

#pragma mark - Login item

int rd_login_item_is_registered(void)
{
    if (@available(macOS 13.0, *)) {
        return SMAppService.mainAppService.status == SMAppServiceStatusEnabled ? 1 : 0;
    }
    return 0;
}

int rd_login_item_register(void)
{
    if (@available(macOS 13.0, *)) {
        NSError *error = nil;
        if (![SMAppService.mainAppService registerAndReturnError:&error]) {
            return RD_ERROR_UNAVAILABLE;
        }
        return RD_OK;
    }
    return RD_ERROR_UNAVAILABLE;
}

int rd_login_item_unregister(void)
{
    if (@available(macOS 13.0, *)) {
        NSError *error = nil;
        if (![SMAppService.mainAppService unregisterAndReturnError:&error]) {
            return RD_ERROR_UNAVAILABLE;
        }
        return RD_OK;
    }
    return RD_ERROR_UNAVAILABLE;
}
