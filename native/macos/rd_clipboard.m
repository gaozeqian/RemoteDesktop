//
//  rd_clipboard.m
//
//  The pasteboard, behind the rd_clipboard_* entry points in rd_native.h.
//
//  Three things here are less obvious than they look:
//
//    * Reads and writes happen on whatever thread the session's clipboard loop runs on, never the main thread. That
//      is fine, and is why this is written against NSPasteboard: the real clipboard lives in a separate process
//      (pboard), so the class is safe to use from any thread. The Carbon Pasteboard Manager has the same property
//      but is far more tedious for pictures, which is why it is not used here.
//
//    * Every entry point takes a pasteboard name. The application always passes NULL for the general pasteboard; the
//      tests pass a private one, so that running them does not overwrite whatever the user had copied. A test that
//      clobbers the clipboard is a test nobody runs twice, and the failure it causes — "my copy disappeared" — looks
//      like anything but a test.
//
//    * The read buffer is shared by every pasteboard, because a read result is documented to live only until the
//      next read. One buffer with a lock is enough, and it keeps a picture-sized allocation out of the steady state.
//
//  There is no permission to check here. Unlike Screen Recording, the general pasteboard is not behind TCC. macOS 15
//  and later do post a "pasted from" notification when a process reads the pasteboard without the user asking, which
//  is a courtesy to the user rather than a gate — and this layer only reads the *content* after the change count
//  moved, never on a timer.
//

#import "rd_native.h"

#import <AppKit/AppKit.h>
#import <os/lock.h>
#import <stdlib.h>
#import <string.h>

static void RDClipboardSetError(char *error, size_t errorSize, NSString *message)
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

/// Resolves the pasteboard to work on. NULL or empty means the general pasteboard.
///
/// `pasteboardWithName:` creates the pasteboard if it does not exist yet, which is what the tests want and is
/// harmless for the general one, which always exists.
static NSPasteboard *RDClipboardForName(const char *name)
{
    if (name == NULL || name[0] == '\0') {
        return [NSPasteboard generalPasteboard];
    }

    NSString *resolved = [NSString stringWithUTF8String:name];
    return resolved != nil ? [NSPasteboard pasteboardWithName:resolved] : nil;
}

/// The buffer a read hands back, reused between reads.
static uint8_t *RDReadBuffer = NULL;
static size_t RDReadCapacity = 0;

/// Guards the read buffer. Reads are documented as serialized, but nothing enforces that: the session loop and a
/// test can both be in here.
static os_unfair_lock RDClipboardLock = OS_UNFAIR_LOCK_INIT;

/// Copies into the shared read buffer, growing it as needed. Returns the destination, or NULL when out of memory.
static uint8_t *RDClipboardStore(const void *bytes, size_t length)
{
    if (length == 0 || bytes == NULL) {
        return NULL;
    }

    if (RDReadCapacity < length) {
        uint8_t *grown = realloc(RDReadBuffer, length);
        if (grown == NULL) {
            return NULL;
        }
        RDReadBuffer = grown;
        RDReadCapacity = length;
    }

    memcpy(RDReadBuffer, bytes, length);
    return RDReadBuffer;
}

/// PNG bytes for whatever picture the pasteboard holds, or nil.
///
/// PNG first because it is already the format the protocol carries; TIFF second because that is what most macOS
/// applications actually publish. Passing up a picture because it arrived in the older type would be silly.
static NSData *RDClipboardPictureData(NSPasteboard *pasteboard)
{
    NSData *png = [pasteboard dataForType:NSPasteboardTypePNG];
    if (png.length > 0) {
        return png;
    }

    NSData *tiff = [pasteboard dataForType:NSPasteboardTypeTIFF];
    if (tiff.length == 0) {
        return nil;
    }

    // TIFF to PNG through AppKit, which also validates the data: imageRepWithData: answers nil for something that
    // claims to be TIFF and is not.
    NSBitmapImageRep *representation = [NSBitmapImageRep imageRepWithData:tiff];
    if (representation == nil) {
        return nil;
    }

    return [representation representationUsingType:NSBitmapImageFileTypePNG properties:@{}];
}

/// The body of rd_clipboard_read, with the lock already held.
static int RDClipboardReadLocked(const char *name, rd_clipboard_content *out)
{
    @autoreleasepool {
        @try {
            NSPasteboard *pasteboard = RDClipboardForName(name);
            if (pasteboard == nil) {
                return RD_ERROR_UNAVAILABLE;
            }

            // Text before pictures: a spreadsheet selection publishes both, and the text is the one the user meant.
            NSString *text = [pasteboard stringForType:NSPasteboardTypeString];
            if (text.length > 0) {
                NSData *utf8 = [text dataUsingEncoding:NSUTF8StringEncoding];
                uint8_t *stored = RDClipboardStore(utf8.bytes, utf8.length);
                if (stored == NULL) {
                    return RD_ERROR_UNAVAILABLE;
                }

                out->kind = RD_CLIPBOARD_TEXT;
                out->bytes = stored;
                out->length = utf8.length;
                return RD_OK;
            }

            NSData *picture = RDClipboardPictureData(pasteboard);
            if (picture.length > 0) {
                uint8_t *stored = RDClipboardStore(picture.bytes, picture.length);
                if (stored == NULL) {
                    return RD_ERROR_UNAVAILABLE;
                }

                out->kind = RD_CLIPBOARD_PNG;
                out->bytes = stored;
                out->length = picture.length;
                return RD_OK;
            }

            // Nothing representable, which the header says is not an error.
            return RD_OK;
        } @catch (NSException *exception) {
            return RD_ERROR_UNAVAILABLE;
        }
    }
}

/// The body of rd_clipboard_write_text, with the lock already held.
static int RDClipboardWriteTextLocked(NSPasteboard *pasteboard,
                                      const char *utf8,
                                      size_t length,
                                      char *error,
                                      size_t errorSize)
{
    @autoreleasepool {
        @try {
            NSString *text = [[NSString alloc] initWithBytes:utf8 length:length encoding:NSUTF8StringEncoding];
            if (text == nil) {
                RDClipboardSetError(error, errorSize, @"The text is not valid UTF-8.");
                return RD_ERROR_ARGUMENT;
            }

            [pasteboard clearContents];
            if (![pasteboard setString:text forType:NSPasteboardTypeString]) {
                RDClipboardSetError(error, errorSize, @"The pasteboard refused the text.");
                return RD_ERROR_UNAVAILABLE;
            }

            return RD_OK;
        } @catch (NSException *exception) {
            RDClipboardSetError(error, errorSize, exception.reason ?: @"The pasteboard rejected the write.");
            return RD_ERROR_UNAVAILABLE;
        }
    }
}

/// The body of rd_clipboard_write_png, with the lock already held.
static int RDClipboardWritePngLocked(NSPasteboard *pasteboard,
                                     const uint8_t *png,
                                     size_t length,
                                     char *error,
                                     size_t errorSize)
{
    @autoreleasepool {
        @try {
            NSData *data = [NSData dataWithBytes:png length:length];

            [pasteboard clearContents];
            if (![pasteboard setData:data forType:NSPasteboardTypePNG]) {
                RDClipboardSetError(error, errorSize, @"The pasteboard refused the picture.");
                return RD_ERROR_UNAVAILABLE;
            }

            // Also offered as TIFF: some applications only ask for the older type. A conversion failure is not worth
            // failing the write over, because the PNG — the type this protocol actually carries — is already on.
            NSBitmapImageRep *representation = [NSBitmapImageRep imageRepWithData:data];
            NSData *tiff = representation != nil
                ? [representation representationUsingType:NSBitmapImageFileTypeTIFF properties:@{}]
                : nil;
            if (tiff.length > 0) {
                [pasteboard setData:tiff forType:NSPasteboardTypeTIFF];
            }

            return RD_OK;
        } @catch (NSException *exception) {
            RDClipboardSetError(error, errorSize, exception.reason ?: @"The pasteboard rejected the write.");
            return RD_ERROR_UNAVAILABLE;
        }
    }
}

int64_t rd_clipboard_change_count(const char *name)
{
    int64_t count = -1;

    os_unfair_lock_lock(&RDClipboardLock);
    @autoreleasepool {
        @try {
            NSPasteboard *pasteboard = RDClipboardForName(name);
            if (pasteboard != nil) {
                count = (int64_t)pasteboard.changeCount;
            }
        } @catch (NSException *exception) {
            count = -1;
        }
    }
    os_unfair_lock_unlock(&RDClipboardLock);

    return count;
}

int rd_clipboard_read(const char *name, rd_clipboard_content *out)
{
    if (out == NULL) {
        return RD_ERROR_ARGUMENT;
    }

    memset(out, 0, sizeof(*out));

    os_unfair_lock_lock(&RDClipboardLock);
    const int status = RDClipboardReadLocked(name, out);
    os_unfair_lock_unlock(&RDClipboardLock);

    return status;
}

int rd_clipboard_write_text(const char *name, const char *utf8, size_t length, char *error, size_t error_size)
{
    if (utf8 == NULL && length > 0) {
        RDClipboardSetError(error, error_size, @"rd_clipboard_write_text was given a length but no text.");
        return RD_ERROR_ARGUMENT;
    }

    os_unfair_lock_lock(&RDClipboardLock);

    int status;
    @autoreleasepool {
        NSPasteboard *pasteboard = RDClipboardForName(name);
        status = pasteboard != nil
            ? RDClipboardWriteTextLocked(pasteboard, utf8, length, error, error_size)
            : RD_ERROR_UNAVAILABLE;
    }

    os_unfair_lock_unlock(&RDClipboardLock);
    return status;
}

int rd_clipboard_write_png(const char *name, const uint8_t *png, size_t length, char *error, size_t error_size)
{
    if (png == NULL || length == 0) {
        RDClipboardSetError(error, error_size, @"rd_clipboard_write_png was given no picture.");
        return RD_ERROR_ARGUMENT;
    }

    os_unfair_lock_lock(&RDClipboardLock);

    int status;
    @autoreleasepool {
        NSPasteboard *pasteboard = RDClipboardForName(name);
        status = pasteboard != nil
            ? RDClipboardWritePngLocked(pasteboard, png, length, error, error_size)
            : RD_ERROR_UNAVAILABLE;
    }

    os_unfair_lock_unlock(&RDClipboardLock);
    return status;
}
