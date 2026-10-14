// Helpers shared by the VideoToolbox encoder and decoder, in native/macos/rd_video_common.m.
//
// These exist as their own translation unit because both directions need them and neither should carry the other:
// the status mapping and the error buffer are mechanical, while the two bitstream conversions are the actual
// substance of this layer and are worth being able to test on their own.

#ifndef RD_VIDEO_COMMON_H
#define RD_VIDEO_COMMON_H

#include <CoreMedia/CoreMedia.h>
#include <CoreVideo/CoreVideo.h>
#include <VideoToolbox/VideoToolbox.h>

#include "rd_native.h"

// NAL unit types this layer recognises. The managed side mirrors these in H264Bitstream, which is what decodes
// what this layer produces, so the two must agree.
#define RD_H264_NAL_SLICE 1
#define RD_H264_NAL_IDR 5
#define RD_H264_NAL_SPS 7
#define RD_H264_NAL_PPS 8

/// The Annex-B start code. Four bytes rather than three: a decoder accepts both, and the four-byte form is what
/// the rest of the pipeline's writers emit.
extern const uint8_t RDVideoStartCode[4];

/// Fills a caller's message buffer, tolerating a null or zero-length buffer.
void RDVideoSetError(char *error, size_t error_size, const char *message);

/// Maps a VideoToolbox OSStatus onto this helper's status codes.
int RDVideoStatusForError(OSStatus status);

/// Total byte size of every parameter set an H.264 format description carries.
size_t RDVideoParameterSetTotalSize(CMFormatDescriptionRef description);

/// Appends every parameter set of a description as Annex-B. Returns bytes written.
size_t RDVideoAppendParameterSets(CMFormatDescriptionRef description, uint8_t *destination, size_t capacity);

/// Converts VideoToolbox's AVCC packing (one NAL per four-byte big-endian length prefix) into Annex-B.
///
/// The length prefix's size has to be read out of the data rather than assumed — it is not always four, and a
/// hard-coded walk desynchronises on the first stream that disagrees.
size_t RDVideoAvccToAnnexB(const uint8_t *source, size_t source_size, uint8_t *destination, size_t capacity);

/// The inverse: splits Annex-B into NALs and records a pointer and size for each.
///
/// `nal_pointers` and `nal_sizes` receive up to `capacity` entries and the return value is how many were found.
/// A count larger than `capacity` means the caller's array was too small, which is a caller bug rather than a
/// reason to silently drop NALs.
size_t RDVideoAnnexBToAvcc(const uint8_t *source, size_t source_size,
                           const uint8_t **nal_pointers, size_t *nal_sizes, size_t capacity);

#endif
