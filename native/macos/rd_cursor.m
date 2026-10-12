//
//  rd_cursor.m
//
//  The pointer, behind rd_cursor_read in rd_native.h.
//
//  Two halves with very different reliability, which is worth stating plainly because the managed side treats them
//  differently:
//
//    * Position comes from CoreGraphics and always works. It needs no permission and no AppKit.
//    * The shape comes from AppKit's NSCursor, and macOS only exposes it inside a process that has an NSApplication.
//      That is true of the real .app and false of a headless test run, so a read that cannot get a shape reports
//      RD_OK with `bgra == NULL` rather than failing — the caller then simply does not raise a cursor-shape event.
//    * Visibility has no public query at all. There is no CoreGraphics or AppKit call that answers "is the pointer
//      currently hidden", so it is always reported as visible. The consequence is that a controller draws a pointer
//      during full-screen video where the host has hidden it; that is a cosmetic gap, and closing it would mean
//      reaching for a private WindowServer API, which is not a trade worth making here.
//

#import "rd_native.h"

#import <AppKit/AppKit.h>
#import <CoreGraphics/CoreGraphics.h>
#import <math.h>
#import <os/lock.h>
#import <stdlib.h>
#import <string.h>

/// The pointer state kept between reads, so `changed` can mean something.
@interface RDCursorState : NSObject
/// Fingerprint of the shape reported last time: dimensions, hot spot and a hash of the pixels.
@property (nonatomic, assign) uint64_t shapeFingerprint;
/// Reused so a per-frame read does not allocate.
@property (nonatomic, assign) uint8_t *shapeBuffer;
@property (nonatomic, assign) size_t shapeCapacity;
@end

@implementation RDCursorState
@end

static RDCursorState *RDCursor(void)
{
    static RDCursorState *state = nil;
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        state = [[RDCursorState alloc] init];
    });
    return state;
}

/// Serializes reads: the capture pipeline calls this from its own thread, but nothing guarantees one caller.
static os_unfair_lock RDCursorLock = OS_UNFAIR_LOCK_INIT;

/// Whether AppKit can be asked about the pointer at all.
///
/// NSApplicationLoad must run on the main thread, and in the real app Avalonia has already created the application
/// by the time capture starts, so this normally just observes an existing NSApp. A headless run never has one, and
/// there the answer stays NO forever — which is exactly the degradation the header describes.
static BOOL RDAppKitReady(void)
{
    static BOOL ready = NO;
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        @try {
            if (NSApp == nil && [NSThread isMainThread]) {
                NSApplicationLoad();
            }
            ready = NSApp != nil;
        } @catch (NSException *exception) {
            ready = NO;
        }
    });
    return ready;
}

/// FNV-1a over the shape, so an unchanged pointer produces an unchanged fingerprint.
static uint64_t RDFingerprint(int32_t width, int32_t height, int32_t hotspotX, int32_t hotspotY,
                              const uint8_t *pixels, size_t length)
{
    uint64_t hash = 1469598103934665603ULL;
    const uint64_t prime = 1099511628211ULL;

    uint64_t header[4] = {(uint64_t)width, (uint64_t)height, (uint64_t)hotspotX, (uint64_t)hotspotY};
    for (int i = 0; i < 4; i++) {
        hash = (hash ^ header[i]) * prime;
    }

    for (size_t i = 0; i < length; i++) {
        hash = (hash ^ pixels[i]) * prime;
    }

    return hash;
}

/// Renders an NSCursor's image into top-down BGRA, reusing the state's buffer.
///
/// Returns NO when there is nothing to report, leaving the caller's snapshot without a shape.
static BOOL RDTryReadShape(RDCursorState *state, rd_cursor_snapshot *out)
{
    NSCursor *cursor = [NSCursor currentSystemCursor];
    NSImage *image = cursor.image;
    if (image == nil) {
        return NO;
    }

    CGImageRef cgImage = [image CGImageForProposedRect:NULL context:nil hints:nil];
    if (cgImage == NULL) {
        return NO;
    }

    const size_t width = CGImageGetWidth(cgImage);
    const size_t height = CGImageGetHeight(cgImage);
    if (width == 0 || height == 0 || width > 512 || height > 512) {
        return NO;
    }

    const size_t stride = width * 4;
    const size_t length = stride * height;
    if (state.shapeCapacity < length) {
        uint8_t *grown = realloc(state.shapeBuffer, length);
        if (grown == NULL) {
            return NO;
        }
        state.shapeBuffer = grown;
        state.shapeCapacity = length;
    }

    // The hot spot is in the image's point space, which is not the pixel space when the cursor is a 2x asset.
    NSPoint hotSpot = cursor.hotSpot;
    const double scale = image.size.width > 0 ? (double)width / image.size.width : 1.0;
    const int32_t hotspotX = (int32_t)llround(hotSpot.x * scale);
    const int32_t hotspotY = (int32_t)llround(hotSpot.y * scale);

    // The buffer is cleared first, and that is not optional: CoreGraphics leaves the RGB of fully-transparent
    // pixels undefined, so drawing over whatever was there before produced a different result on every call. That
    // made the change-detection fingerprint below fire on every single read, which would have raised a
    // cursor-shape event for every frame. Measured: with a reused buffer the drawn hash differed on all five
    // iterations while the source image's own hash stayed identical; clearing first made all five agree.
    memset(state.shapeBuffer, 0, length);

    CGColorSpaceRef colorSpace = CGColorSpaceCreateDeviceRGB();
    if (colorSpace == NULL) {
        return NO;
    }

    // Alpha-premultiplied-first with little-endian word order is BGRA byte order, which is what the capture path
    // produces and what the protocol expects. Drawing into a context whose user space spans the image puts the
    // image's top row at buffer row 0, matching the captured frames' top-down layout.
    CGContextRef context = CGBitmapContextCreate(state.shapeBuffer, width, height, 8, stride, colorSpace,
                                                 kCGImageAlphaPremultipliedFirst | kCGBitmapByteOrder32Little);
    CGColorSpaceRelease(colorSpace);
    if (context == NULL) {
        return NO;
    }

    CGContextDrawImage(context, CGRectMake(0, 0, (CGFloat)width, (CGFloat)height), cgImage);
    CGContextRelease(context);

    out->bgra = state.shapeBuffer;
    out->width = (int32_t)width;
    out->height = (int32_t)height;
    out->hotspot_x = hotspotX;
    out->hotspot_y = hotspotY;

    const uint64_t fingerprint = RDFingerprint(out->width, out->height, hotspotX, hotspotY, state.shapeBuffer, length);
    out->changed = fingerprint != state.shapeFingerprint;
    state.shapeFingerprint = fingerprint;
    return YES;
}

int rd_cursor_read(rd_cursor_snapshot *out)
{
    if (out == NULL) {
        return RD_ERROR_ARGUMENT;
    }

    memset(out, 0, sizeof(*out));

    os_unfair_lock_lock(&RDCursorLock);
    @autoreleasepool {
        CGEventRef event = CGEventCreate(NULL);
        if (event == NULL) {
            os_unfair_lock_unlock(&RDCursorLock);
            return RD_ERROR_UNAVAILABLE;
        }

        CGPoint location = CGEventGetLocation(event);
        CFRelease(event);

        out->x = location.x;
        out->y = location.y;
        // No public API reports whether the pointer is hidden; see the note at the top of the file.
        out->visible = 1;

        if (RDAppKitReady()) {
            @try {
                RDTryReadShape(RDCursor(), out);
            } @catch (NSException *exception) {
                // A shape is a nicety; never let it take the capture down.
                out->bgra = NULL;
                out->changed = 0;
            }
        }
    }

    os_unfair_lock_unlock(&RDCursorLock);
    return RD_OK;
}
