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
    /// No camera with the requested identifier is connected.
    RD_ERROR_DEVICE_NOT_FOUND = -7,
    /// No audio device could be opened for playback.
    RD_ERROR_AUDIO_DEVICE = -8,
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

/// Camera permission state, mirroring AVAuthorizationStatus.
///
/// Reported as its own enum rather than as a bool because "not decided yet" and "refused" need different words in
/// front of the user: the first is a prompt, the second is a trip to System Settings.
enum {
    RD_CAMERA_NOT_DETERMINED = 0,
    RD_CAMERA_RESTRICTED = 1,
    RD_CAMERA_DENIED = 2,
    RD_CAMERA_AUTHORIZED = 3,
};

/// Whether this process may use the camera, as AVAuthorizationStatus.
///
/// Read without prompting, so it is safe to call on a timer. Like Screen Recording, the answer is cached by the
/// process at start-up: a grant made while this process runs is only seen by the next one.
RD_EXPORT int rd_camera_authorization_status(void);

/// Asks for camera access, showing the system prompt the first time.
///
/// Blocks until the user answers, so it must not be called on the main thread. Returns the state afterwards —
/// which, for the same reason as above, may still be "not granted" from the running process's point of view.
RD_EXPORT int rd_camera_request_access(void);

/// One camera the system knows about.
typedef struct {
    /// AVCaptureDevice.uniqueID, UTF-8.
    const char *id;
    /// The name to show the user, UTF-8.
    ///
    /// Both strings are owned by this library and stay valid until the next rd_camera_* call that returns a list,
    /// so a caller that wants to keep them has to copy first — the same rule as the clipboard read buffer.
    const char *name;
} rd_camera_info;

/// One picture size and rate a camera offers.
typedef struct {
    int32_t width;
    int32_t height;
    double frame_rate;
} rd_camera_mode;

/// Lists the connected cameras.
///
/// `*out` receives a library-owned array valid until the next listing call. No cameras is RD_OK with a zero count,
/// not an error: a machine without one is a normal machine.
RD_EXPORT int rd_camera_list(const rd_camera_info **out,
                             int32_t *out_count,
                             char *error,
                             size_t error_size);

/// Lists the modes one camera offers, largest first.
///
/// A camera that cannot be opened right now (in use by another application, or unplugged between the two calls)
/// still enumerates with no modes rather than failing, which is what the Windows implementation does — the reason
/// it cannot be used is reported when it is actually opened.
RD_EXPORT int rd_camera_modes(const char *id,
                              const rd_camera_mode **out,
                              int32_t *out_count,
                              char *error,
                              size_t error_size);

/// One picture, handed to rd_camera_frame_callback.
///
/// As with rd_frame, the pixels point into a buffer that is only valid for the duration of the callback.
typedef struct {
    /// Tightly-or-row-padded BGRA pixels, `height` rows of `bytes_per_row` bytes.
    const uint8_t *bgra;
    int32_t width;
    int32_t height;
    int32_t bytes_per_row;
    /// Mach host ticks converted to nanoseconds, on the same clock the display capture reports.
    uint64_t host_time_ns;
} rd_camera_frame;

/// Called on the session's private queue, once per picture.
///
/// Delivery is serialized, so the callee needs no locking of its own. A camera has no "nothing changed" state the
/// way a display does: every callback carries a new picture.
typedef void (*rd_camera_frame_callback)(void *user, const rd_camera_frame *frame);

/// Opaque camera handle.
typedef void *rd_camera_ref;

/// Opens one camera at the size and rate closest to the request. Does not start it.
///
/// Fails with RD_ERROR_PERMISSION when the user has not granted camera access, and with
/// RD_ERROR_DEVICE_NOT_FOUND when the identifier no longer names a connected camera. The mode is chosen by scoring
/// the camera's own formats, because AVFoundation has no notion of asking a device for an arbitrary size.
RD_EXPORT int rd_camera_create(const char *id,
                               int32_t width,
                               int32_t height,
                               double frame_rate,
                               rd_camera_frame_callback callback,
                               void *user,
                               rd_camera_ref *out,
                               char *error,
                               size_t error_size);

/// Starts delivering pictures. Blocks while the session starts, so it must not be called on the main thread.
RD_EXPORT int rd_camera_start(rd_camera_ref camera, char *error, size_t error_size);

/// Stops delivering pictures. Safe on a camera that was never started, and safe to call twice.
RD_EXPORT void rd_camera_stop(rd_camera_ref camera);

/// Stops if needed, releases everything, and invalidates the handle. No frames arrive after this returns.
RD_EXPORT void rd_camera_destroy(rd_camera_ref camera);

/// One chunk of captured audio, handed to rd_audio_callback.
///
/// The samples are interleaved 32-bit float and only valid for the duration of the callback.
typedef struct {
    /// `frame_count * channel_count` floats, channel-interleaved.
    const float *samples;
    /// Sample frames, not samples: this times `channel_count` is the number of floats.
    int32_t frame_count;
    int32_t channel_count;
    int32_t sample_rate;
} rd_audio_chunk;

/// Called on the stream's private queue with each chunk of captured audio.
///
/// Delivery is serialized, so the callee needs no locking of its own. Unlike a display stream, nothing is filtered
/// out here: silence that the system actually renders is delivered, because it is part of what the far end should
/// hear. The stream simply delivers nothing while no application is playing.
typedef void (*rd_audio_callback)(void *user, const rd_audio_chunk *chunk);

/// Opaque system-audio capture handle.
typedef void *rd_audio_capture_ref;

/// Starts capturing what the machine is playing, through ScreenCaptureKit.
///
/// This is loopback, not a microphone: it needs the Screen Recording grant and no microphone grant, and it captures
/// the whole system's output. Audio from this process is excluded, because the host plays the far end's audio
/// through the same process and capturing it back would close a loop.
///
/// The stream is fixed at 48 kHz stereo, which is what the wire format carries, so nothing has to resample.
RD_EXPORT int rd_audio_capture_create(rd_audio_callback callback,
                                      void *user,
                                      rd_audio_capture_ref *out,
                                      char *error,
                                      size_t error_size);

/// Starts delivering audio. Blocks until ScreenCaptureKit confirms the start.
RD_EXPORT int rd_audio_capture_start(rd_audio_capture_ref capture, char *error, size_t error_size);

/// Stops delivering audio. Safe on a capture that was never started, and safe to call twice.
RD_EXPORT void rd_audio_capture_stop(rd_audio_capture_ref capture);

/// Stops if needed, releases everything, and invalidates the handle.
RD_EXPORT void rd_audio_capture_destroy(rd_audio_capture_ref capture);

/// Opaque playback handle.
typedef void *rd_audio_player_ref;

/// Opens a playback device for interleaved 32-bit float audio.
///
/// Does not start it. The device is the system default output, followed automatically if the user switches it.
RD_EXPORT int rd_audio_player_create(int32_t sample_rate,
                                     int32_t channel_count,
                                     rd_audio_player_ref *out,
                                     char *error,
                                     size_t error_size);

/// Starts playing, and from here on the device asks for audio continuously.
///
/// Until something is queued it plays silence, so an underrun is a pause rather than an error — the same choice the
/// Windows player makes by reading fully from its buffer.
RD_EXPORT int rd_audio_player_start(rd_audio_player_ref player, char *error, size_t error_size);

/// Queues interleaved float samples.
///
/// When the device has fallen behind — the queue already holds more than this layer keeps — the oldest audio is
/// dropped to make room, trading a click for bounded latency rather than playing late.
RD_EXPORT int rd_audio_player_enqueue(rd_audio_player_ref player,
                                      const float *samples,
                                      size_t sample_count,
                                      char *error,
                                      size_t error_size);

/// Samples queued but not yet played, or -1 for an invalid handle.
///
/// A count rather than a duration because the caller already knows the format it opened the device with; the
/// managed layer turns it into the TimeSpan the port contract asks for.
RD_EXPORT int64_t rd_audio_player_buffered(rd_audio_player_ref player);

/// Stops playing and discards anything queued. Safe on a player that was never started.
RD_EXPORT void rd_audio_player_stop(rd_audio_player_ref player);

/// Throws away everything queued but keeps playing, so the next enqueue is heard immediately.
///
/// Distinct from stopping and starting again, which would also tear down the device. The managed layer uses this to
/// hold latency to the bound its port contract asks for: the ring is a full second deep so that it cannot grow
/// without limit, but a second of delay is not something a remote session can live with, so the policy lives above
/// this layer and the ring is the safety net underneath it.
RD_EXPORT void rd_audio_player_discard(rd_audio_player_ref player);

/// Stops if needed, releases everything, and invalidates the handle.
RD_EXPORT void rd_audio_player_destroy(rd_audio_player_ref player);

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
