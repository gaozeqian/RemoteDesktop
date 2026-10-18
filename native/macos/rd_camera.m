//
//  rd_camera.m
//
//  AVFoundation behind the C ABI in rd_native.h.
//
//  The awkward parts, and how they are handled here:
//
//    * A camera has no "ask it for 1280x720" call. AVFoundation offers a list of formats the device supports, so the
//      requested size and rate are matched by scoring that list. The session preset is then set to
//      AVCaptureSessionPresetInputPriority, which is the one preset that tells the session not to overwrite the
//      activeFormat we just chose — with any other preset the choice is silently undone.
//    * Pictures arrive through an AVCaptureVideoDataOutput delegate on a queue we supply. That queue is serial, so
//      the callback never runs concurrently with itself and the callee needs no locking of its own.
//    * The CVPixelBuffer is only valid inside the delegate call. Nothing is retained across it.
//    * `alwaysDiscardsLateVideoFrames` is what gives "only the newest picture": a slow consumer misses frames rather
//      than building a backlog, which is the same choice the Windows source reader makes.
//    * The list-returning calls hand back library-owned memory that has to survive until the next such call. That is
//      what RDListReset exists for, and why the header says the caller must copy.
//

#import "rd_native.h"

#import <AVFoundation/AVFoundation.h>
#import <CoreMedia/CoreMedia.h>
#import <CoreVideo/CoreVideo.h>
#import <Foundation/Foundation.h>
#import <float.h>
#import <math.h>
#import <os/lock.h>

/// Fills the caller's message buffer, tolerating a null or zero-length buffer.
static void RDCameraSetError(char *error, size_t errorSize, NSString *message)
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

/// Below this a "mode" is not something a remote viewer would ever pick, and virtual cameras advertise a long tail
/// of tiny ones. The same floor the Windows enumerator applies.
static const int32_t RDCameraMinWidth = 160;
static const int32_t RDCameraMinFrameRate = 5;

/// Maps an NSError from AVFoundation onto one of our status codes.
///
/// The three "not authorized" codes all mean the same thing to the caller — the fix is a permission prompt rather
/// than a retry — and the three "in use / gone" codes all mean the camera is unavailable right now.
static int RDCameraStatusForError(NSError *error)
{
    if (error == nil) {
        return RD_OK;
    }

    switch (error.code) {
        case AVErrorApplicationIsNotAuthorizedToUseDevice:
            return RD_ERROR_PERMISSION;
        case AVErrorDeviceNotConnected:
            return RD_ERROR_DEVICE_NOT_FOUND;
        case AVErrorDeviceAlreadyUsedByAnotherSession:
        case AVErrorDeviceInUseByAnotherApplication:
        case AVErrorDeviceLockedForConfigurationByAnotherProcess:
            return RD_ERROR_UNAVAILABLE;
        default:
            return RD_ERROR_STREAM;
    }
}

#pragma mark - Enumerated lists

/// Guards the library-owned storage the listing calls hand out.
static os_unfair_lock RDListLock = OS_UNFAIR_LOCK_INIT;
static char **RDListStrings = NULL;
static size_t RDListStringCount = 0;
static void *RDListPayload = NULL;

/// Frees whatever the previous listing call returned.
static void RDListReset(void)
{
    for (size_t i = 0; i < RDListStringCount; i++) {
        free(RDListStrings[i]);
    }

    free(RDListStrings);
    RDListStrings = NULL;
    RDListStringCount = 0;

    free(RDListPayload);
    RDListPayload = NULL;
}

/// Copies a string into the listing storage and returns the copy, which stays valid until the next listing call.
static const char *RDListIntern(NSString *value)
{
    const char *utf8 = value.UTF8String ?: "";

    // strdup rather than keeping the NSString alive: the C side outlives the autorelease pool this runs in, and the
    // header promises the pointers survive until the next call.
    char *copy = strdup(utf8);
    if (copy == NULL) {
        return "";
    }

    char **grown = realloc(RDListStrings, (RDListStringCount + 1) * sizeof(char *));
    if (grown == NULL) {
        free(copy);
        return "";
    }

    RDListStrings = grown;
    RDListStrings[RDListStringCount++] = copy;
    return copy;
}

/// Allocates the array the listing call returns, replacing any previous one.
static void *RDListAllocate(size_t bytes)
{
    RDListReset();
    RDListPayload = calloc(1, bytes > 0 ? bytes : 1);
    return RDListPayload;
}

#pragma mark - Devices and formats

/// The device types worth listing.
///
/// AVCaptureDeviceTypeExternalUnknown is the pre-14 spelling of AVCaptureDeviceTypeExternal and is deprecated as of
/// the version this library builds against, so only the current name is used. DeskView and Continuity are included
/// because both are real cameras a user can pick on a modern Mac.
static NSArray<AVCaptureDeviceType> *RDCameraDeviceTypes(void)
{
    return @[
        AVCaptureDeviceTypeBuiltInWideAngleCamera,
        AVCaptureDeviceTypeExternal,
        AVCaptureDeviceTypeContinuityCamera,
        AVCaptureDeviceTypeDeskViewCamera,
    ];
}

static NSArray<AVCaptureDevice *> *RDCameraDevices(void)
{
    AVCaptureDeviceDiscoverySession *session =
        [AVCaptureDeviceDiscoverySession discoverySessionWithDeviceTypes:RDCameraDeviceTypes()
                                                               mediaType:AVMediaTypeVideo
                                                                position:AVCaptureDevicePositionUnspecified];
    return session.devices;
}

static AVCaptureDevice *RDCameraFind(NSString *uniqueId)
{
    for (AVCaptureDevice *device in RDCameraDevices()) {
        if ([device.uniqueID isEqualToString:uniqueId]) {
            return device;
        }
    }

    return nil;
}

/// The fastest rate a format supports. Zero when it advertises none, which a virtual camera occasionally does.
static double RDCameraMaxFrameRate(AVCaptureDeviceFormat *format)
{
    double fastest = 0;
    for (AVFrameRateRange *range in format.videoSupportedFrameRateRanges) {
        fastest = MAX(fastest, range.maxFrameRate);
    }

    return fastest;
}

/// The format whose picture size and rate are closest to what was asked for.
static AVCaptureDeviceFormat *RDCameraBestFormat(AVCaptureDevice *device, int32_t width, int32_t height, double frameRate)
{
    AVCaptureDeviceFormat *best = nil;
    double bestScore = DBL_MAX;
    const double requested = (double)width * (double)height;

    for (AVCaptureDeviceFormat *format in device.formats) {
        CMVideoDimensions dimensions = CMVideoFormatDescriptionGetDimensions(format.formatDescription);
        const double area = (double)dimensions.width * (double)dimensions.height;

        // Area difference dominates; the rate is weighted the same way the Windows implementation weights it, so
        // that a big format at the wrong rate loses to a slightly smaller one at the right rate.
        const double score = fabs(area - requested) + (fabs(RDCameraMaxFrameRate(format) - frameRate) * 1000.0);
        if (score < bestScore) {
            best = format;
            bestScore = score;
        }
    }

    return best;
}

/// The requested rate, moved into what the format can actually deliver.
static double RDCameraClampFrameRate(AVCaptureDeviceFormat *format, double frameRate)
{
    double fastest = 0;
    for (AVFrameRateRange *range in format.videoSupportedFrameRateRanges) {
        if (frameRate >= range.minFrameRate && frameRate <= range.maxFrameRate) {
            return frameRate;
        }

        fastest = MAX(fastest, range.maxFrameRate);
    }

    // Nothing covers the request, so take the fastest the format has. A camera that advertises no ranges at all
    // keeps its own default rather than being given an impossible duration.
    return fastest > 0 ? fastest : 0;
}

/// Points the device at a format and rate. Returns NO with `outError` filled when the device refuses to be locked,
/// which is what happens while another application holds it.
static BOOL RDCameraApplyFormat(AVCaptureDevice *device,
                                AVCaptureDeviceFormat *format,
                                double frameRate,
                                NSError **outError)
{
    if (![device lockForConfiguration:outError]) {
        return NO;
    }

    @try {
        device.activeFormat = format;

        const double clamped = RDCameraClampFrameRate(format, frameRate);
        if (clamped > 0) {
            // Round the duration UP, never to nearest. AVFoundation checks the duration against the format's
            // supported range, and 1/30 s rounded to this timescale is 33333/1000000 = 0.033333 s — a frame rate
            // of 30.0003, a hair above the range's 30.00 maximum. It refuses that with an NSException, which is
            // not an NSError and therefore terminates the process rather than failing the call (observed with
            // the built-in MacBook Pro camera). Ceiling keeps the effective rate at or below what was asked for.
            const int64_t timescale = 1000000;
            const int64_t value = (int64_t)ceil((1.0 / clamped) * (double)timescale);
            const CMTime duration = CMTimeMake(value, timescale);
            device.activeVideoMinFrameDuration = duration;
            device.activeVideoMaxFrameDuration = duration;
        }
    } @catch (NSException *exception) {
        // The header promises no exceptions cross this boundary. AVFoundation still raises one for a value it
        // does not like, and an uncaught Objective-C exception takes the whole process down, so it is converted
        // into the same NSError-shaped failure as every other refusal here.
        if (outError != NULL) {
            *outError = [NSError errorWithDomain:@"com.remotedesktop.camera"
                                            code:AVErrorUnknown
                                        userInfo:@{
                                            NSLocalizedDescriptionKey:
                                                exception.reason ?: @"The camera rejected the requested format."
                                        }];
        }

        return NO;
    } @finally {
        [device unlockForConfiguration];
    }

    return YES;
}

#pragma mark - Frame delivery

/// Receives the session's pictures and forwards them.
@interface RDCameraOutput : NSObject <AVCaptureVideoDataOutputSampleBufferDelegate>
@property (nonatomic, assign) rd_camera_frame_callback callback;
@property (nonatomic, assign) void *user;
@end

@implementation RDCameraOutput

- (void)captureOutput:(AVCaptureOutput *)output
    didOutputSampleBuffer:(CMSampleBufferRef)sampleBuffer
           fromConnection:(AVCaptureConnection *)connection
{
    if (self.callback == NULL) {
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
        // The sample's stamp is on the host clock for a capture device, which is the same clock the display
        // capture reports on — so a controller can compare the two without knowing where either came from.
        CMTime presentation = CMSampleBufferGetPresentationTimeStamp(sampleBuffer);
        uint64_t hostTimeNs = 0;
        if (CMTIME_IS_VALID(presentation)) {
            hostTimeNs = (uint64_t)(CMTimeGetSeconds(presentation) * 1e9);
        }

        rd_camera_frame frame = {
            .bgra = (const uint8_t *)CVPixelBufferGetBaseAddress(pixelBuffer),
            .width = (int32_t)CVPixelBufferGetWidth(pixelBuffer),
            .height = (int32_t)CVPixelBufferGetHeight(pixelBuffer),
            .bytes_per_row = (int32_t)CVPixelBufferGetBytesPerRow(pixelBuffer),
            .host_time_ns = hostTimeNs,
        };

        self.callback(self.user, &frame);
    } @finally {
        CVPixelBufferUnlockBaseAddress(pixelBuffer, kCVPixelBufferLock_ReadOnly);
    }
}

@end

#pragma mark - Camera

/// One open (or running) camera. Held by the C handle through a retained bridge, so ARC owns its lifetime.
@interface RDCamera : NSObject
@property (nonatomic, strong) AVCaptureSession *session;
@property (nonatomic, strong) AVCaptureDeviceInput *input;
@property (nonatomic, strong) AVCaptureVideoDataOutput *output;
@property (nonatomic, strong) RDCameraOutput *delegate;
@property (nonatomic, strong) dispatch_queue_t queue;
@property (nonatomic, strong) AVCaptureDevice *device;
@property (nonatomic, strong) AVCaptureDeviceFormat *format;
@property (nonatomic, assign) double frameRate;
@property (nonatomic, assign) BOOL started;
@end

@implementation RDCamera
@end

int rd_camera_authorization_status(void)
{
    switch ([AVCaptureDevice authorizationStatusForMediaType:AVMediaTypeVideo]) {
        case AVAuthorizationStatusRestricted:
            return RD_CAMERA_RESTRICTED;
        case AVAuthorizationStatusDenied:
            return RD_CAMERA_DENIED;
        case AVAuthorizationStatusAuthorized:
            return RD_CAMERA_AUTHORIZED;
        case AVAuthorizationStatusNotDetermined:
        default:
            return RD_CAMERA_NOT_DETERMINED;
    }
}

int rd_camera_request_access(void)
{
    if (rd_camera_authorization_status() != RD_CAMERA_NOT_DETERMINED) {
        // Asking again would return immediately without showing anything, so the current answer is the answer.
        return rd_camera_authorization_status();
    }

    // Blocking here is deliberate: the managed side is a "may I use the camera" call and has nothing useful to do
    // until the user has answered. It must therefore not run on the main thread.
    dispatch_semaphore_t answered = dispatch_semaphore_create(0);
    [AVCaptureDevice requestAccessForMediaType:AVMediaTypeVideo
                             completionHandler:^(BOOL granted) {
                                 (void)granted;
                                 dispatch_semaphore_signal(answered);
                             }];

    // A user who leaves the dialog open forever must not wedge the caller.
    dispatch_semaphore_wait(answered, dispatch_time(DISPATCH_TIME_NOW, (int64_t)(120 * NSEC_PER_SEC)));
    return rd_camera_authorization_status();
}

int rd_camera_list(const rd_camera_info **out, int32_t *out_count, char *error, size_t error_size)
{
    if (out == NULL || out_count == NULL) {
        RDCameraSetError(error, error_size, @"rd_camera_list needs an output array and a count.");
        return RD_ERROR_ARGUMENT;
    }

    *out = NULL;
    *out_count = 0;

    os_unfair_lock_lock(&RDListLock);
    @autoreleasepool {
        NSArray<AVCaptureDevice *> *devices = RDCameraDevices();
        rd_camera_info *list = RDListAllocate(sizeof(rd_camera_info) * devices.count);
        if (list == NULL) {
            os_unfair_lock_unlock(&RDListLock);
            RDCameraSetError(error, error_size, @"Out of memory while listing cameras.");
            return RD_ERROR_UNAVAILABLE;
        }

        int32_t index = 0;
        for (AVCaptureDevice *device in devices) {
            list[index].id = RDListIntern(device.uniqueID);
            list[index].name = RDListIntern(device.localizedName);
            index++;
        }

        *out = list;
        *out_count = index;
    }

    os_unfair_lock_unlock(&RDListLock);
    return RD_OK;
}

int rd_camera_modes(const char *id, const rd_camera_mode **out, int32_t *out_count, char *error, size_t error_size)
{
    if (id == NULL || out == NULL || out_count == NULL) {
        RDCameraSetError(error, error_size, @"rd_camera_modes needs a camera and an output array.");
        return RD_ERROR_ARGUMENT;
    }

    *out = NULL;
    *out_count = 0;

    os_unfair_lock_lock(&RDListLock);
    @autoreleasepool {
        NSString *uniqueId = [NSString stringWithUTF8String:id];
        AVCaptureDevice *device = uniqueId != nil ? RDCameraFind(uniqueId) : nil;
        if (device == nil) {
            os_unfair_lock_unlock(&RDListLock);
            RDCameraSetError(error, error_size, @"That camera is not connected.");
            return RD_ERROR_DEVICE_NOT_FOUND;
        }

        NSArray<AVCaptureDeviceFormat *> *formats = device.formats;

        // Generous: the filters below only ever drop entries, so this cannot overflow.
        rd_camera_mode *list = RDListAllocate(sizeof(rd_camera_mode) * formats.count);
        if (list == NULL) {
            os_unfair_lock_unlock(&RDListLock);
            RDCameraSetError(error, error_size, @"Out of memory while listing camera modes.");
            return RD_ERROR_UNAVAILABLE;
        }

        int32_t count = 0;
        for (AVCaptureDeviceFormat *format in formats) {
            CMVideoDimensions dimensions = CMVideoFormatDescriptionGetDimensions(format.formatDescription);
            const double frameRate = round(RDCameraMaxFrameRate(format));

            // The same floor the Windows enumerator applies: below this a "mode" is not something a remote viewer
            // would ever choose, and virtual cameras advertise a long tail of tiny ones.
            if (dimensions.width < RDCameraMinWidth || dimensions.height < RDCameraMinWidth * 3 / 4 ||
                frameRate < RDCameraMinFrameRate) {
                continue;
            }

            BOOL seen = NO;
            for (int32_t i = 0; i < count; i++) {
                if (list[i].width == dimensions.width && list[i].height == dimensions.height &&
                    list[i].frame_rate == frameRate) {
                    seen = YES;
                    break;
                }
            }

            if (!seen) {
                list[count].width = dimensions.width;
                list[count].height = dimensions.height;
                list[count].frame_rate = frameRate;
                count++;
            }
        }

        // Largest first, then fastest — the order a picker wants to show them in.
        for (int32_t i = 1; i < count; i++) {
            rd_camera_mode value = list[i];
            int32_t j = i - 1;
            while (j >= 0 && (list[j].width * list[j].height < value.width * value.height ||
                              (list[j].width * list[j].height == value.width * value.height &&
                               list[j].frame_rate < value.frame_rate))) {
                list[j + 1] = list[j];
                j--;
            }

            list[j + 1] = value;
        }

        *out = list;
        *out_count = count;
    }

    os_unfair_lock_unlock(&RDListLock);
    return RD_OK;
}

int rd_camera_create(const char *id,
                     int32_t width,
                     int32_t height,
                     double frame_rate,
                     rd_camera_frame_callback callback,
                     void *user,
                     rd_camera_ref *out,
                     char *error,
                     size_t error_size)
{
    if (id == NULL || out == NULL || callback == NULL) {
        RDCameraSetError(error, error_size, @"rd_camera_create needs a camera, an output handle and a callback.");
        return RD_ERROR_ARGUMENT;
    }

    *out = NULL;

    if (width <= 0 || height <= 0) {
        RDCameraSetError(error, error_size, @"The requested camera size must be positive.");
        return RD_ERROR_ARGUMENT;
    }

    @autoreleasepool {
        // Checked up front rather than left to the session, because a session without the grant does not fail — it
        // starts and delivers nothing, which is the worst possible way to learn about a missing permission.
        const int authorization = rd_camera_authorization_status();
        if (authorization != RD_CAMERA_AUTHORIZED) {
            RDCameraSetError(error, error_size, @"Camera access has not been granted to this application.");
            return RD_ERROR_PERMISSION;
        }

        NSString *uniqueId = [NSString stringWithUTF8String:id];
        AVCaptureDevice *device = uniqueId != nil ? RDCameraFind(uniqueId) : nil;
        if (device == nil) {
            RDCameraSetError(error, error_size, @"That camera is not connected.");
            return RD_ERROR_DEVICE_NOT_FOUND;
        }

        NSError *inputError = nil;
        AVCaptureDeviceInput *input = [AVCaptureDeviceInput deviceInputWithDevice:device error:&inputError];
        if (input == nil) {
            RDCameraSetError(error, error_size, inputError.localizedDescription ?: @"The camera could not be opened.");
            return RDCameraStatusForError(inputError);
        }

        AVCaptureDeviceFormat *format = RDCameraBestFormat(device, width, height, frame_rate);
        if (format == nil) {
            RDCameraSetError(error, error_size, @"The camera offers no video format.");
            return RD_ERROR_UNAVAILABLE;
        }

        AVCaptureSession *session = [[AVCaptureSession alloc] init];
        [session beginConfiguration];

        if (![session canAddInput:input]) {
            [session commitConfiguration];
            RDCameraSetError(error, error_size, @"The capture session refused the camera.");
            return RD_ERROR_UNAVAILABLE;
        }

        [session addInput:input];

        AVCaptureVideoDataOutput *output = [[AVCaptureVideoDataOutput alloc] init];

        // BGRA is what the rest of the pipeline already carries — the display capturer produces it and the encoder
        // consumes it — so no colour conversion is inserted here. The key is a CFStringRef, hence the bridge cast:
        // a plain (id) is not a legal cast under ARC.
        output.videoSettings = @{(__bridge NSString *)kCVPixelBufferPixelFormatTypeKey: @(kCVPixelFormatType_32BGRA)};

        // Only the newest picture: a consumer that falls behind misses frames instead of working through a backlog.
        output.alwaysDiscardsLateVideoFrames = YES;

        if (![session canAddOutput:output]) {
            [session commitConfiguration];
            RDCameraSetError(error, error_size, @"The capture session refused the video output.");
            return RD_ERROR_UNAVAILABLE;
        }

        [session addOutput:output];

        RDCameraOutput *delegate = [[RDCameraOutput alloc] init];
        delegate.callback = callback;
        delegate.user = user;

        // A serial queue is what makes the callback single-threaded from the callee's point of view.
        dispatch_queue_t queue = dispatch_queue_create("com.remotedesktop.camera", DISPATCH_QUEUE_SERIAL);
        [output setSampleBufferDelegate:delegate queue:queue];

        [session commitConfiguration];

        // The format is applied *after* the session is configured, and that order is the whole trick on macOS.
        // iOS has AVCaptureSessionPresetInputPriority, which tells the session to leave the device's activeFormat
        // alone — but it is marked API_UNAVAILABLE(macos), and there is no macOS equivalent. On macOS the session
        // instead re-derives activeFormat from its preset while it is being configured, so a format set before
        // commitConfiguration is silently replaced. Setting it afterwards sticks, and the session leaves it alone
        // from then on because the preset never changes again.
        NSError *lockError = nil;
        if (!RDCameraApplyFormat(device, format, frame_rate, &lockError)) {
            RDCameraSetError(error, error_size, lockError.localizedDescription ?: @"The camera is busy.");
            return RDCameraStatusForError(lockError);
        }

        RDCamera *camera = [[RDCamera alloc] init];
        camera.session = session;
        camera.input = input;
        camera.output = output;
        camera.delegate = delegate;
        camera.queue = queue;
        camera.device = device;
        camera.format = format;
        camera.frameRate = frame_rate;

        *out = (__bridge_retained void *)camera;
        return RD_OK;
    }
}

int rd_camera_start(rd_camera_ref handle, char *error, size_t error_size)
{
    RDCamera *camera = (__bridge RDCamera *)handle;
    if (camera == nil) {
        RDCameraSetError(error, error_size, @"The camera handle is not valid.");
        return RD_ERROR_ARGUMENT;
    }

    if (camera.started) {
        return RD_OK;
    }

    // startRunning is synchronous and can take a noticeable moment while the device spins up, which is why the
    // header says not to call this from the main thread. It can also raise rather than return an error when the
    // session is in a state it refuses; the C boundary must report that, not let it kill the process.
    @try {
        [camera.session startRunning];
    } @catch (NSException *exception) {
        RDCameraSetError(error, error_size, exception.reason ?: @"The camera session could not be started.");
        return RD_ERROR_UNAVAILABLE;
    }

    // Apply the chosen format once more, now that the session is running. The first application happens after
    // commitConfiguration (see rd_camera_create), which is enough to make it stick for a session that is not yet
    // running — but startRunning re-derives the device format from the session preset one last time. Without this
    // second application a request for 1280x720 came back as 1920x1080, and the controller's mode picker was
    // describing something the stream was not. Changing the format on a running session is allowed; the output
    // delivers the next pictures in the new size, which the module already handles as a stream change.
    NSError *formatError = nil;
    if (!RDCameraApplyFormat(camera.device, camera.format, camera.frameRate, &formatError)) {
        [camera.session stopRunning];
        RDCameraSetError(error, error_size, formatError.localizedDescription ?: @"The camera rejected the requested format.");
        return RD_ERROR_UNAVAILABLE;
    }

    camera.started = YES;
    return RD_OK;
}

void rd_camera_stop(rd_camera_ref handle)
{
    RDCamera *camera = (__bridge RDCamera *)handle;
    if (camera == nil || !camera.started) {
        return;
    }

    camera.started = NO;
    [camera.session stopRunning];
}

void rd_camera_destroy(rd_camera_ref handle)
{
    if (handle == NULL) {
        return;
    }

    // Take ownership back from the bridge; ARC releases it at the end of this scope.
    RDCamera *camera = (__bridge_transfer RDCamera *)handle;
    rd_camera_stop((__bridge void *)camera);

    // Clearing the callback before the session is released closes the window where a picture in flight could still
    // reach managed memory that is being torn down.
    camera.delegate.callback = NULL;
    camera.delegate.user = NULL;
    camera.session = nil;
    camera.output = nil;
    camera.input = nil;
    camera.delegate = nil;
}
