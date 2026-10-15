using System.Runtime.InteropServices;

// Every struct here is blittable (a C bool is one byte): the runtime's exports take pointers to them with no marshalling.

namespace PlayMap.Media.Video.Ndi;

// The layouts and constants below are transcribed from the NDI SDK 6.3 headers (Processing.NDI.structs.h, .Find.h, .Recv.h,
// .Send.h, .SendAdvertiser.h, .RecvAdvertiser.h), which carry this notice:
//
// Copyright (C) 2023-2026 Vizrt NDI AB. All rights reserved.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
// associated documentation files(the "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and / or sell
// copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the
// following conditions :
//
// The above copyright notice and this permission notice shall be included in all copies or substantial
// portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT
// LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO
// EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
// IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR
// THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//
// NDI® is a registered trademark of Vizrt NDI AB. No part of the NDI runtime ships with PlayMap: the user's own install is
// loaded at run time (NdiRuntime).

/// <summary>The NDI frame types a capture answers.</summary>
internal enum NdiFrameType
{
    None = 0,
    Video = 1,
    Audio = 2,
    Metadata = 3,
    Error = 4,
    StatusChange = 100,
    SourceChange = 101,
}

/// <summary>NDI's FourCC codes for video (and the planar float audio code, 'FLTp', for the record: not 'FLTP').</summary>
internal static class NdiFourCc
{
    public const int Uyvy = 0x59565955;
    public const int Uyva = 0x41565955;
    public const int P216 = 0x36313250;
    public const int Pa16 = 0x36314150;
    public const int Bgra = 0x41524742;
    public const int Bgrx = 0x58524742;
    public const int AudioFltp = 0x70544C46;
}

/// <summary>The colour formats a receiver asks for: fastest gives UYVY (UYVA with alpha), best gives P216/PA16 from native senders.</summary>
internal static class NdiColorFormat
{
    public const int Fastest = 100;
    public const int Best = 101;
}

internal static class NdiBandwidth
{
    public const int Lowest = 0;
    public const int Highest = 100;
}

internal static class NdiFrameFormat
{
    public const int Progressive = 1;
}

/// <summary>NDIlib_source_t.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NdiSourceT
{
    public nint NdiName;
    public nint UrlAddress;
}

/// <summary>NDIlib_video_frame_v2_t (72 bytes on 64-bit).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NdiVideoFrame
{
    public int XRes;
    public int YRes;
    public int FourCc;
    public int FrameRateN;
    public int FrameRateD;
    public float PictureAspectRatio;
    public int FrameFormatType;
    public long Timecode;
    public nint Data;
    public int LineStrideInBytes;
    public nint Metadata;
    public long Timestamp;
}

/// <summary>NDIlib_metadata_frame_t.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NdiMetadataFrame
{
    public int Length;
    public long Timecode;
    public nint Data;
}

/// <summary>NDIlib_tally_t.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NdiTally
{
    public byte OnProgram;
    public byte OnPreview;
}

/// <summary>NDIlib_find_create_t.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NdiFindCreate
{
    public byte ShowLocalSources;
    public nint Groups;
    public nint ExtraIps;
}

/// <summary>NDIlib_recv_create_v3_t.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NdiRecvCreate
{
    public NdiSourceT SourceToConnectTo;
    public int ColorFormat;
    public int Bandwidth;
    public byte AllowVideoFields;
    public nint RecvName;
}

/// <summary>NDIlib_send_create_t.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NdiSendCreate
{
    public nint NdiName;
    public nint Groups;
    public byte ClockVideo;
    public byte ClockAudio;
}

/// <summary>NDIlib_send_advertiser_create_t and NDIlib_recv_advertiser_create_t (the discovery server's URL).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NdiAdvertiserCreate
{
    public nint UrlAddress;
}

/// <summary>The receive timestamp NDI uses for "none".</summary>
internal static class NdiTimestamps
{
    public const long Undefined = long.MaxValue;
}
