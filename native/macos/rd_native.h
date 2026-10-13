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
    /// The codec refused the configuration, or the bitstream cannot be decoded.
    RD_ERROR_CODEC = -9,
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

#pragma mark - Video

/// One access unit, already in Annex-B form.
///
/// The bytes belong to the encoder and stay valid only until the next `rd_video_encoder_encode` call, so a caller
/// that keeps them must copy first. Every access unit the caller receives must be sent: dropping one desynchronises
/// the decoder's reference pictures.
typedef struct {
    /// Annex-B bitstream: a start code (00 00 00 01) before each NAL, parameter sets included on key frames.
    const uint8_t *data;
    size_t length;

    /// Non-zero when this access unit carries an IDR picture.
    int32_t is_key_frame;
} rd_video_packet;

/// Called on VideoToolbox's own thread when a compressed picture becomes available.
///
/// The encoder is not driven from here; the callback only records that something arrived and the entry point picks
/// it up. Delivery is serialized by VideoToolbox, so the callee needs no locking of its own.
typedef void (*rd_video_encoder_callback)(void *user);

/// Opaque video encoder handle.
typedef void *rd_video_encoder_ref;

/// Creates a VideoToolbox compression session.
///
/// - `codec` is a four-character code: 'avc1' for H.264 (kCMVideoCodecType_H264).
/// - `prefer_software` selects the software encoder over the hardware one when both exist. On Apple Silicon the
///   hardware encoder is the sensible default; the software one exists so a caller can fall back when the hardware
///   one refuses a configuration or is busy.
/// - `output_packets`/`output_capacity` are not used at creation; they belong to `rd_video_encoder_encode`.
/// - `name_out`/`is_hardware_out` report which encoder actually accepted the configuration, because the two are
///   chosen by a lookup that can fail and fall back — a caller that assumed would report the wrong backend.
///
/// Returns RD_OK, or a negative status with a message in `error`.
RD_EXPORT int rd_video_encoder_create(
    int32_t codec,
    int32_t width,
    int32_t height,
    int32_t frame_rate,
    int32_t bitrate_kbps,
    int32_t prefer_software,
    rd_video_encoder_callback callback,
    void *user,
    rd_video_encoder_ref *out,
    char *name_out,
    size_t name_size,
    int32_t *is_hardware_out,
    char *error,
    size_t error_size);

/// Adjusts the bitrate while the session runs, in kbit/s.
RD_EXPORT int rd_video_encoder_set_bitrate(rd_video_encoder_ref encoder, int32_t bitrate_kbps);

/// Presents one NV12 frame of exactly the configured width × height, tightly packed (stride == width).
///
/// Returns RD_OK with whatever access units became ready: usually one, none while the encoder is buffering, or more
/// than one when a late output arrives together with this picture. All of them must be sent onward.
RD_EXPORT int rd_video_encoder_encode(
    rd_video_encoder_ref encoder,
    const uint8_t *nv12,
    size_t nv12_size,
    int32_t force_key_frame,
    int64_t timestamp_ns,
    rd_video_packet *output_packets,
    int32_t output_capacity,
    int32_t *count_out,
    int32_t timeout_ms,
    char *error,
    size_t error_size);

/// Stops the session and releases everything.
RD_EXPORT void rd_video_encoder_destroy(rd_video_encoder_ref encoder);

/// Called on VideoToolbox's own thread when a decoded picture becomes available.
typedef void (*rd_video_decoder_callback)(void *user);

/// Opaque video decoder handle.
typedef void *rd_video_decoder_ref;

/// Creates a VideoToolbox decompression session.
///
/// The session starts without a format description: the first access unit carries the SPS and PPS it needs, and the
/// description is rebuilt whenever those change. That is what makes the decoder usable on a stream whose dimensions
/// are not known in advance.
RD_EXPORT int rd_video_decoder_create(
    int32_t codec,
    rd_video_decoder_callback callback,
    void *user,
    rd_video_decoder_ref *out,
    char *error,
    size_t error_size);

/// One decoded picture, as two planes.
///
/// As with every other output of this helper, the bytes belong to the decoder and stay valid only until the next
/// call; the caller copies them out.
typedef struct {
    uint8_t *y;
    uint8_t *uv; ///< Interleaved CbCr plane, half the height of Y.
    int32_t y_stride;
    int32_t uv_stride;
    int32_t width;
    int32_t height;

    /// Non-zero when a picture was produced. Zero without an error means "nothing ready yet".
    int32_t has_frame;
} rd_video_picture;

/// Decodes one Annex-B access unit.
///
/// Returns RD_OK with `picture` describing the picture that became ready (possibly none, when the access unit was
/// not a picture or the decoder is still filling its pipeline), RD_ERROR_UNAVAILABLE when the bitstream cannot be
/// decoded (the caller should request a key frame), or another negative status with a message in `error`.
RD_EXPORT int rd_video_decoder_decode(
    rd_video_decoder_ref decoder,
    const uint8_t *annex_b,
    size_t annex_b_size,
    int64_t timestamp_ns,
    int32_t timeout_ms,
    rd_video_picture *picture,
    char *error,
    size_t error_size);

/// Stops the session and releases everything.
RD_EXPORT void rd_video_decoder_destroy(rd_video_decoder_ref decoder);

#pragma mark - Recording

/// Opaque video recorder handle.
typedef void *rd_recorder_ref;

/// Creates a recorder that encodes the frames handed to it into an H.264 MP4 at `path`.
///
/// `path` is a UTF-8 filesystem path; the parent directory must already exist. `frame_rate` is the timeline the file
/// presents, because the caller hands frames over without timestamps and the recorder times them itself. `bitrate_kbps`
/// is the target encoder bitrate; 0 lets AVFoundation choose.
///
/// The encoding and the MP4 muxing are both done by AVAssetWriter, which drives VideoToolbox underneath — so this is
/// the same H.264 engine as the live pipeline, only pointed at a file instead of the wire. Returns RD_OK, or a negative
/// status with `error` filled. The handle must be released with rd_recorder_destroy.
RD_EXPORT int rd_recorder_create(const char *path,
                                 int32_t width,
                                 int32_t height,
                                 int32_t frame_rate,
                                 int32_t bitrate_kbps,
                                 rd_recorder_ref *out,
                                 char *error,
                                 size_t error_size);

/// Hands one BGRA32 frame (top row first) to the recorder.
///
/// `bgra` is `height` rows of `stride` bytes, where `stride >= width * 4`. A row shorter than that is a programming
/// error: it is rejected with RD_ERROR_ARGUMENT rather than cropped silently. The recorder copies the pixels into a
/// pixel buffer it owns, so `bgra` need not outlive the call.
///
/// Returns RD_OK, or a negative status with `error` filled when the frame could not be appended (writer error, or
/// backpressure that could not be relieved within a few seconds). The first such failure marks the recording broken;
/// later frames are ignored.
RD_EXPORT int rd_recorder_append(rd_recorder_ref recorder,
                                 const uint8_t *bgra,
                                 size_t length,
                                 int32_t stride,
                                 int32_t width,
                                 int32_t height,
                                 char *error,
                                 size_t error_size);

/// Finalizes the file.
///
/// Sets `*out_wrote_frames` to the number of frames accepted. Returns RD_OK and a positive count when a recording was
/// written; RD_OK with a zero count when no frame ever arrived (in which case no file is left behind); and RD_ERROR_CODEC
/// when the writer genuinely failed to finalize a recording that did start. The caller turns the zero count into
/// "nothing was recorded" and the codec error into "the recording could not be saved".
RD_EXPORT int rd_recorder_finish(rd_recorder_ref recorder,
                                 int64_t *out_wrote_frames,
                                 char *error,
                                 size_t error_size);

/// Stops if needed and releases everything. Safe to call twice.
RD_EXPORT void rd_recorder_destroy(rd_recorder_ref recorder);

/// Reads the first frame of an MP4 back and reports the luma of its top and bottom rows, to prove a recording was
/// written the right way up. `out_top_luma` and `out_bottom_luma` receive 0..255 averages. Returns RD_OK, or
/// RD_ERROR_CODEC when the file cannot be read.
///
/// This exists only so a self-check and a test can verify that the bytes handed in top-row-first come back the same
/// way — the one orientation mistake the contract is最容易 to make, and the one a solid-colour frame cannot reveal.
RD_EXPORT int rd_recorder_probe_orientation(const char *path,
                                            double *out_top_luma,
                                            double *out_bottom_luma,
                                            char *error,
                                            size_t error_size);

#pragma mark - Login item

/// Whether this application is registered to launch when the user logs in.
///
/// Reports 0 (not registered) on macOS before 13, where SMAppService does not exist. The state is keyed to the
/// signed bundle, so an ad-hoc signature reports a fresh, unregistered identity on every build.
RD_EXPORT int rd_login_item_is_registered(void);

/// Registers this application to launch at login via SMAppService.mainAppService.
///
/// Returns RD_OK, or RD_ERROR_UNAVAILABLE when the system refuses (not in /Applications, an ad-hoc signature, or
/// macOS before 13). Registration is best-effort at the system level: it returns success when the request was
/// accepted, not when the user has approved anything.
RD_EXPORT int rd_login_item_register(void);

/// Unregisters this application from launching at login.
///
/// Returns RD_OK, or RD_ERROR_UNAVAILABLE when the system refuses (the same conditions as rd_login_item_register).
RD_EXPORT int rd_login_item_unregister(void);

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
