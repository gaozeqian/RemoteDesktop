#import "rd_video_common.h"

#import <stdlib.h>
#import <string.h>

const uint8_t RDVideoStartCode[4] = {0x00, 0x00, 0x00, 0x01};

void RDVideoSetError(char *error, size_t error_size, const char *message)
{
    if (error == NULL || error_size == 0) {
        return;
    }

    if (message == NULL) {
        error[0] = '\0';
        return;
    }

    strncpy(error, message, error_size - 1);
    error[error_size - 1] = '\0';
}

int RDVideoStatusForError(OSStatus status)
{
    // A refused configuration, a property that does not exist, and a session that will not take the frame in its
    // current state are all "the platform will not do this" — surfaced as an argument or unsupported problem
    // rather than as a codec failure, so the caller knows retrying the same thing differently is the answer.
    switch (status) {
        case kVTParameterErr:
        case kVTPropertyNotSupportedErr:
        case kVTPropertyReadOnlyErr:
        case kVTInvalidSessionErr:
            return RD_ERROR_ARGUMENT;

        // Nothing to encode or decode with: no encoder for that configuration, or a session the system cannot
        // currently provide. Both are transient or configurational, not "the bitstream is broken".
        case kVTCouldNotFindVideoEncoderErr:
        case kVTCouldNotFindVideoDecoderErr:
        case kVTCouldNotCreateInstanceErr:
        case kVTAllocationFailedErr:
        case kVTVideoEncoderNotAvailableNowErr:
        case kVTVideoDecoderNotAvailableNowErr:
        case kVTVideoEncoderMalfunctionErr:
        case kVTVideoDecoderMalfunctionErr:
        case kVTPixelTransferNotSupportedErr:
        case kVTPixelTransferNotPermittedErr:
            return RD_ERROR_UNAVAILABLE;

        // The data itself is wrong, which is the one case the managed layer turns into "request a key frame".
        case kVTVideoDecoderBadDataErr:
        case kVTVideoDecoderUnsupportedDataFormatErr:
        case kVTInsufficientSourceColorDataErr:
        case kVTFormatDescriptionChangeNotSupportedErr:
            return RD_ERROR_CODEC;

        default:
            return status == noErr ? RD_OK : RD_ERROR_CODEC;
    }
}

size_t RDVideoParameterSetTotalSize(CMFormatDescriptionRef description)
{
    size_t total = 0;
    size_t count = 0;
    if (description == NULL) {
        return 0;
    }

    if (CMVideoFormatDescriptionGetH264ParameterSetAtIndex(description, 0, NULL, NULL, &count, NULL) != noErr) {
        return 0;
    }

    for (size_t i = 0; i < count; i++) {
        size_t parameterSetSize = 0;
        if (CMVideoFormatDescriptionGetH264ParameterSetAtIndex(description, i, NULL, &parameterSetSize, NULL, NULL) != noErr) {
            continue;
        }

        total += parameterSetSize;
    }

    return total;
}

size_t RDVideoAppendParameterSets(CMFormatDescriptionRef description, uint8_t *destination, size_t capacity)
{
    size_t written = 0;
    size_t count = 0;
    if (description == NULL) {
        return 0;
    }

    if (CMVideoFormatDescriptionGetH264ParameterSetAtIndex(description, 0, NULL, NULL, &count, NULL) != noErr) {
        return 0;
    }

    for (size_t i = 0; i < count; i++) {
        const uint8_t *parameterSet = NULL;
        size_t parameterSetSize = 0;
        if (CMVideoFormatDescriptionGetH264ParameterSetAtIndex(description, i, &parameterSet, &parameterSetSize, NULL, NULL) != noErr) {
            continue;
        }

        if (written + sizeof(RDVideoStartCode) + parameterSetSize > capacity) {
            // The caller sized its buffer from the access unit plus the parameter sets' own total, so running out
            // means that total was wrong. Truncating a parameter set is worse than omitting it: a decoder takes
            // the whole access unit down with it.
            break;
        }

        memcpy(destination + written, RDVideoStartCode, sizeof(RDVideoStartCode));
        written += sizeof(RDVideoStartCode);
        memcpy(destination + written, parameterSet, parameterSetSize);
        written += parameterSetSize;
    }

    return written;
}

size_t RDVideoAvccToAnnexB(const uint8_t *source, size_t source_size, uint8_t *destination, size_t capacity)
{
    size_t written = 0;
    size_t position = 0;

    while (position + 4 <= source_size) {
        const uint32_t nalLength = ((uint32_t)source[position] << 24)
            | ((uint32_t)source[position + 1] << 16)
            | ((uint32_t)source[position + 2] << 8)
            | (uint32_t)source[position + 3];
        position += 4;

        if (nalLength == 0 || position + nalLength > source_size) {
            // A truncated trailing NAL is dropped rather than emitted half-formed; a decoder would fail the whole
            // access unit on it anyway, and emitting it changes a recoverable stream into an unrecoverable one.
            break;
        }

        if (written + sizeof(RDVideoStartCode) + nalLength > capacity) {
            break;
        }

        memcpy(destination + written, RDVideoStartCode, sizeof(RDVideoStartCode));
        written += sizeof(RDVideoStartCode);
        memcpy(destination + written, source + position, nalLength);
        written += nalLength;
        position += nalLength;
    }

    return written;
}

/// Reports whether a start code begins at `position`, and how long it is: three or four bytes.
static BOOL RDVideoIsStartCodeAt(const uint8_t *data, size_t size, size_t position, size_t *codeLengthOut)
{
    if (position + 3 > size) {
        return NO;
    }

    if (data[position] == 0x00 && data[position + 1] == 0x00 && data[position + 2] == 0x01) {
        *codeLengthOut = 3;
        return YES;
    }

    if (position + 4 <= size && data[position] == 0x00 && data[position + 1] == 0x00
        && data[position + 2] == 0x00 && data[position + 3] == 0x01) {
        *codeLengthOut = 4;
        return YES;
    }

    return NO;
}

size_t RDVideoAnnexBToAvcc(const uint8_t *source, size_t source_size,
                           const uint8_t **nal_pointers, size_t *nal_sizes, size_t capacity)
{
    // Trailing zero bytes are start-code padding, not NAL payload, and a NAL that carries them is invalid. Trimmed
    // once here rather than reasoned about per NAL, because reasoning about it per NAL is where an off-by-one
    // hides: a buffer ending "… 00 00 00" leaks a zero into the last NAL otherwise.
    while (source_size > 0 && source[source_size - 1] == 0x00) {
        source_size--;
    }

    size_t count = 0;
    size_t position = 0;

    while (position + 3 <= source_size && count < capacity) {
        size_t codeLength = 0;
        if (!RDVideoIsStartCodeAt(source, source_size, position, &codeLength)) {
            position++;
            continue;
        }

        const size_t nalStart = position + codeLength;

        // The NAL runs to the next start code, or to the end of the trimmed buffer. A NAL cannot itself contain a
        // start code — emulation prevention sees to that — so the first one found ends it unambiguously.
        size_t scan = nalStart;
        while (scan + 3 <= source_size) {
            size_t ignored = 0;
            if (RDVideoIsStartCodeAt(source, source_size, scan, &ignored)) {
                break;
            }

            scan++;
        }

        const size_t nalEnd = scan + 3 <= source_size ? scan : source_size;
        if (nalEnd > nalStart) {
            nal_pointers[count] = source + nalStart;
            nal_sizes[count] = nalEnd - nalStart;
            count++;
        }

        position = nalEnd;
    }

    return count;
}
