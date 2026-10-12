//
//  rd_native.h
//
//  The flat C ABI the managed macOS platform layer binds to.
//
//  Why this exists: ScreenCaptureKit is Objective-C only, and its shape is the worst case for P/Invoke — an async
//  factory, a delegate protocol for frame delivery, and block-based completion handlers. Expressing that directly
//  from C# would mean hand-building Objective-C message sends, selector registration and block trampolines, all of
//  which are unstable ABI. Instead the Objective-C lives here, in one small file, and crosses the boundary as plain
//  C: opaque handles, a struct, and a function pointer.
//
//  Rules this header keeps:
//
//    * No Objective-C types cross the boundary. `id` becomes `void *` at most.
//    * Every pointer handed to a callback is only valid for the duration of that callback. The callee copies.
//    * Failures are an integer status plus a caller-supplied message buffer. No exceptions, no NSError objects.
//    * The caller owns and frees the handle through rd_capture_destroy; nothing is released implicitly.
//

#ifndef RD_NATIVE_H
#define RD_NATIVE_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/// Marks the functions that make up the ABI.
///
/// The library is compiled with -fvisibility=hidden, which keeps Objective-C runtime symbols and everything else
/// out of the exported table. Without this every entry point would be hidden too, and the failure mode is a
/// dylib that builds and signs cleanly and then fails at the first DllImport with "entry point not found".
#define RD_EXPORT __attribute__((visibility("default")))

/// A rectangle in captured-frame pixels.
typedef struct {
    int32_t x;
    int32_t y;
    int32_t width;
    int32_t height;
} rd_rect;

/// Status codes. Anything other than RD_OK is a failure and the message buffer holds the reason.
enum {
    RD_OK = 0,
    /// A caller-supplied argument was out of range or null.
    RD_ERROR_ARGUMENT = -1,
    /// Screen Recording has not been granted to this process.
    RD_ERROR_PERMISSION = -2,
    /// No display with the requested identifier is currently shareable.
    RD_ERROR_DISPLAY_NOT_FOUND = -3,
    /// ScreenCaptureKit refused to start or stopped with an error.
    RD_ERROR_STREAM = -4,
    /// The requested operation did not finish in time.
    RD_ERROR_TIMEOUT = -5,
    /// The platform does not offer what was asked for (e.g. no cursor image is available).
    RD_ERROR_UNAVAILABLE = -6,
};

/// One captured frame, handed to rd_frame_callback.
///
/// The pixel and rectangle pointers are only valid until the callback returns: they point straight into the
/// CVPixelBuffer ScreenCaptureKit lent us, and that buffer goes back to the pool afterwards. The callee must copy.
typedef struct {
    /// Tightly-or-row-padded BGRA pixels, `height` rows of `bytes_per_row` bytes.
    const uint8_t *bgra;
    int32_t width;
    int32_t height;
    int32_t bytes_per_row;
    /// Mach host ticks converted to nanoseconds, from the frame's display time.
    uint64_t host_time_ns;
    /// Regions that changed since the previous frame. Empty means the whole frame should be treated as changed.
    const rd_rect *dirty_rects;
    int32_t dirty_count;
} rd_frame;

/// Called on the stream's private queue, once per frame that actually has new content.
///
/// Frames that ScreenCaptureKit marks as idle, blank, suspended or stopped are *not* delivered: the managed side
/// treats "no callback within the timeout" as "the screen did not change", which is exactly the distinction the
/// IScreenCapturer contract is built on. Delivery is serialized, so the callee does not need its own locking.
typedef void (*rd_frame_callback)(void *user, const rd_frame *frame);

/// Opaque capture handle.
typedef void *rd_capture_ref;

/// Creates a capture for one display. Does not start it.
///
/// `pixel_width` and `pixel_height` are the size the frames should be produced at, in pixels — the display's
/// backing size, not its point size. Passing the point size on a Retina display would capture at half resolution.
///
/// This calls into ScreenCaptureKit's asynchronous shareable-content lookup and blocks until it answers, because
/// the only alternative is to make every caller deal with a two-phase construction for no benefit. On success
/// `*out` receives a handle that must be released with rd_capture_destroy.
RD_EXPORT int rd_capture_create(uint32_t display_id,
                                int32_t pixel_width,
                                int32_t pixel_height,
                                int32_t frames_per_second,
                                rd_frame_callback callback,
                                void *user,
                                rd_capture_ref *out,
                                char *error,
                                size_t error_size);

/// Starts delivering frames. Blocks until ScreenCaptureKit confirms the start.
RD_EXPORT int rd_capture_start(rd_capture_ref capture, char *error, size_t error_size);

/// Stops delivering frames. Safe to call on a capture that was never started, and safe to call twice.
RD_EXPORT void rd_capture_stop(rd_capture_ref capture);

/// Stops if needed, releases everything, and invalidates the handle. No frames arrive after this returns.
RD_EXPORT void rd_capture_destroy(rd_capture_ref capture);

/// The pointer, read at the moment of the call.
typedef struct {
    /// Global display coordinates, top-left origin, in points — the space CGEvent positions use.
    double x;
    double y;
    int32_t visible;

    /// Non-zero when the pointer shape differs from the previous rd_cursor_read call.
    int32_t changed;
    /// BGRA pixels for the current shape, or NULL when the shape is unavailable.
    ///
    /// Owned by this library and valid until the next rd_cursor_read on the same thread; the caller must copy.
    const uint8_t *bgra;
    int32_t width;
    int32_t height;
    int32_t hotspot_x;
    int32_t hotspot_y;
} rd_cursor_snapshot;

/// Reads the pointer.
///
/// Returns RD_OK with a filled snapshot, or RD_ERROR_UNAVAILABLE when the pointer cannot be read at all. A
/// successful read with `bgra == NULL` is normal: macOS does not expose a shape for every cursor.
///
/// This exists because ScreenCaptureKit can composite the pointer into the image (`showsCursor`) but never reports
/// its shape, and the remote-control protocol wants the controller to draw the real pointer — which is also what
/// the Windows capture does, so both platforms behave the same way.
RD_EXPORT int rd_cursor_read(rd_cursor_snapshot *out);

/// What a clipboard read produced.
enum {
    /// The pasteboard holds nothing this layer can represent. Not an error.
    RD_CLIPBOARD_NONE = 0,
    /// `bytes` is UTF-8 text and `length` is its byte length; it is not NUL-terminated.
    RD_CLIPBOARD_TEXT = 1,
    /// `bytes` is a complete PNG file.
    RD_CLIPBOARD_PNG = 2,
};

/// One clipboard read.
///
/// `bytes` is owned by this library and stays valid until the next read of *any* pasteboard, so a caller that wants
/// to keep it has to copy before reading again.
typedef struct {
    int32_t kind;
    const uint8_t *bytes;
    size_t length;
} rd_clipboard_content;

/// The pasteboard's change count, or -1 when it cannot be read at all.
///
/// Only "is it different from last time" means anything; the number itself is opaque and not comparable across
/// processes. Polling this is how the managed side notices that something else took the clipboard — NSPasteboard
/// posts no notification for that, and polling the *content* instead would mean pulling a picture the user never
/// asked to share into this process on every tick.
///
/// `name` is the pasteboard name, or NULL for the general pasteboard. Taking the name is what makes this testable:
/// a test can work on a private pasteboard rather than overwriting whatever the user had copied.
RD_EXPORT int64_t rd_clipboard_change_count(const char *name);

/// Reads the pasteboard.
///
/// Text wins over a picture when both are present, because copying a range of cells out of a spreadsheet puts both
/// there and the text is what the user meant — the same rule the Windows implementation follows. A picture is taken
/// from PNG, or converted from TIFF when that is all the writer offered, since TIFF is still what most macOS
/// applications put on the pasteboard.
///
/// Returns RD_OK with `kind == RD_CLIPBOARD_NONE` for an empty or unrepresentable pasteboard.
RD_EXPORT int rd_clipboard_read(const char *name, rd_clipboard_content *out);

/// Replaces the pasteboard contents with UTF-8 text. `length` is the byte length, excluding any terminator.
RD_EXPORT int rd_clipboard_write_text(const char *name,
                                      const char *utf8,
                                      size_t length,
                                      char *error,
                                      size_t error_size);

/// Replaces the pasteboard contents with a PNG.
///
/// The picture is offered as PNG and, when it converts, also as TIFF, so applications that only look for the older
/// type can still paste it.
RD_EXPORT int rd_clipboard_write_png(const char *name,
                                     const uint8_t *png,
                                     size_t length,
                                     char *error,
                                     size_t error_size);

/// Opens System Settings on the Privacy & Security pane named by `anchor`, e.g. "Privacy_ScreenCapture".
///
/// Returns RD_OK once the request has been handed to the workspace. It does not wait for the pane to appear, and it
/// cannot tell whether the user then grants anything — that is what rd_screen_recording_allowed-style checks are
/// for, and they have to be re-read after the user comes back.
///
/// Used instead of spawning `/usr/bin/open` so the application does not launch helper processes to do something
/// AppKit can do itself.
RD_EXPORT int rd_open_settings(const char *anchor);

#ifdef __cplusplus
}
#endif

#endif // RD_NATIVE_H
