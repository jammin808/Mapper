// PlayMap's own test double for the NDI 6 runtime. The tests compile this file into libndi.so.6 and load it in
// place of the user's NDI runtime: a tiny in-process NDI (senders, receivers, finders, tally, advertisers) plus
// fake_ndi_* control exports through which the tests inject sources and frames and read back what the NDI edge
// did. It never ships and never touches the network.
//
//   cc -O1 -std=c11 -Wall -fPIC -shared -pthread fake_ndi.c -o libndi.so.6
//
// The struct layouts, enumerations and constants below are transcribed from the NDI SDK 6.3 headers
// (Processing.NDI.structs.h, .Find.h, .Recv.h, .Send.h, .SendAdvertiser.h, .RecvAdvertiser.h), which carry
// this notice:
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

#define _DEFAULT_SOURCE
#define _POSIX_C_SOURCE 200809L

#include <errno.h>
#include <pthread.h>
#include <stdarg.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/mman.h>
#include <time.h>
#include <unistd.h>

#define FAKE_API __attribute__((visibility("default")))

// ===== Transcribed from the NDI SDK 6.3 headers =================================================================

#define NDI_LIB_FOURCC(ch0, ch1, ch2, ch3) \
	((uint32_t)(uint8_t)(ch0) | ((uint32_t)(uint8_t)(ch1) << 8) | ((uint32_t)(uint8_t)(ch2) << 16) | ((uint32_t)(uint8_t)(ch3) << 24))

typedef enum NDIlib_frame_type_e {
	NDIlib_frame_type_none = 0,
	NDIlib_frame_type_video = 1,
	NDIlib_frame_type_audio = 2,
	NDIlib_frame_type_metadata = 3,
	NDIlib_frame_type_error = 4,
	NDIlib_frame_type_status_change = 100,
	NDIlib_frame_type_source_change = 101,
	NDIlib_frame_type_max = 0x7fffffff
} NDIlib_frame_type_e;

typedef enum NDIlib_FourCC_video_type_e {
	NDIlib_FourCC_video_type_UYVY = NDI_LIB_FOURCC('U', 'Y', 'V', 'Y'),
	NDIlib_FourCC_video_type_UYVA = NDI_LIB_FOURCC('U', 'Y', 'V', 'A'),
	NDIlib_FourCC_video_type_P216 = NDI_LIB_FOURCC('P', '2', '1', '6'),
	NDIlib_FourCC_video_type_PA16 = NDI_LIB_FOURCC('P', 'A', '1', '6'),
	NDIlib_FourCC_video_type_BGRA = NDI_LIB_FOURCC('B', 'G', 'R', 'A'),
	NDIlib_FourCC_video_type_BGRX = NDI_LIB_FOURCC('B', 'G', 'R', 'X'),
	NDIlib_FourCC_video_type_max = 0x7fffffff
} NDIlib_FourCC_video_type_e;

typedef enum NDIlib_FourCC_audio_type_e {
	NDIlib_FourCC_audio_type_FLTP = NDI_LIB_FOURCC('F', 'L', 'T', 'p'),
	NDIlib_FourCC_audio_type_max = 0x7fffffff
} NDIlib_FourCC_audio_type_e;

typedef enum NDIlib_frame_format_type_e {
	NDIlib_frame_format_type_progressive = 1,
	NDIlib_frame_format_type_interleaved = 0,
	NDIlib_frame_format_type_field_0 = 2,
	NDIlib_frame_format_type_field_1 = 3,
	NDIlib_frame_format_type_max = 0x7fffffff
} NDIlib_frame_format_type_e;

typedef enum NDIlib_recv_bandwidth_e {
	NDIlib_recv_bandwidth_metadata_only = -10,
	NDIlib_recv_bandwidth_audio_only = 10,
	NDIlib_recv_bandwidth_lowest = 0,
	NDIlib_recv_bandwidth_highest = 100,
	NDIlib_recv_bandwidth_max = 0x7fffffff
} NDIlib_recv_bandwidth_e;

typedef enum NDIlib_recv_color_format_e {
	NDIlib_recv_color_format_BGRX_BGRA = 0,
	NDIlib_recv_color_format_UYVY_BGRA = 1,
	NDIlib_recv_color_format_RGBX_RGBA = 2,
	NDIlib_recv_color_format_UYVY_RGBA = 3,
	NDIlib_recv_color_format_fastest = 100,
	NDIlib_recv_color_format_best = 101,
	NDIlib_recv_color_format_max = 0x7fffffff
} NDIlib_recv_color_format_e;

typedef struct NDIlib_source_t {
	const char* p_ndi_name;
	union {
		const char* p_url_address;
		const char* p_ip_address;
	};
} NDIlib_source_t;

typedef struct NDIlib_video_frame_v2_t {
	int xres, yres;
	NDIlib_FourCC_video_type_e FourCC;
	int frame_rate_N, frame_rate_D;
	float picture_aspect_ratio;
	NDIlib_frame_format_type_e frame_format_type;
	int64_t timecode;
	uint8_t* p_data;
	union {
		int line_stride_in_bytes;
		int data_size_in_bytes;
	};
	const char* p_metadata;
	int64_t timestamp;
} NDIlib_video_frame_v2_t;

// The fake never produces audio, so the audio frames are opaque here: v2 is only passed through by pointer, and
// v3 has the header's size and alignment on 64-bit targets (the layout probe checks it).
typedef struct NDIlib_audio_frame_v2_t NDIlib_audio_frame_v2_t;
typedef struct NDIlib_audio_frame_v3_t {
	int64_t opaque[8];
} NDIlib_audio_frame_v3_t;
_Static_assert(sizeof(void*) == 8, "the opaque audio frame size holds for 64-bit targets only");

typedef struct NDIlib_metadata_frame_t {
	int length;
	int64_t timecode;
	char* p_data;
} NDIlib_metadata_frame_t;

typedef struct NDIlib_tally_t {
	bool on_program;
	bool on_preview;
} NDIlib_tally_t;

typedef struct NDIlib_find_create_t {
	bool show_local_sources;
	const char* p_groups;
	const char* p_extra_ips;
} NDIlib_find_create_t;

typedef struct NDIlib_recv_create_v3_t {
	NDIlib_source_t source_to_connect_to;
	NDIlib_recv_color_format_e color_format;
	NDIlib_recv_bandwidth_e bandwidth;
	bool allow_video_fields;
	const char* p_ndi_recv_name;
} NDIlib_recv_create_v3_t;

typedef struct NDIlib_send_create_t {
	const char* p_ndi_name;
	const char* p_groups;
	bool clock_video, clock_audio;
} NDIlib_send_create_t;

typedef struct NDIlib_send_advertiser_create_t {
	const char* p_url_address;
} NDIlib_send_advertiser_create_t;

typedef struct NDIlib_recv_advertiser_create_t {
	const char* p_url_address;
} NDIlib_recv_advertiser_create_t;

typedef struct NDIlib_find_instance_type* NDIlib_find_instance_t;
typedef struct NDIlib_recv_instance_type* NDIlib_recv_instance_t;
typedef struct NDIlib_send_instance_type* NDIlib_send_instance_t;
typedef struct NDIlib_send_advertiser_instance_type* NDIlib_send_advertiser_instance_t;
typedef struct NDIlib_recv_advertiser_instance_type* NDIlib_recv_advertiser_instance_t;

// ===== PlayMap's fake ===========================================================================================

// sizeof and offsetof values fake_ndi_layout writes, in this order; layout_probe.c prints the same list from the
// real headers.
#define FAKE_NDI_LAYOUT(S, F) \
	S(NDIlib_source_t) F(NDIlib_source_t, p_ndi_name) F(NDIlib_source_t, p_url_address) \
	S(NDIlib_video_frame_v2_t) F(NDIlib_video_frame_v2_t, xres) F(NDIlib_video_frame_v2_t, yres) \
	F(NDIlib_video_frame_v2_t, FourCC) F(NDIlib_video_frame_v2_t, frame_rate_N) \
	F(NDIlib_video_frame_v2_t, frame_rate_D) F(NDIlib_video_frame_v2_t, picture_aspect_ratio) \
	F(NDIlib_video_frame_v2_t, frame_format_type) F(NDIlib_video_frame_v2_t, timecode) \
	F(NDIlib_video_frame_v2_t, p_data) F(NDIlib_video_frame_v2_t, line_stride_in_bytes) \
	F(NDIlib_video_frame_v2_t, p_metadata) F(NDIlib_video_frame_v2_t, timestamp) \
	S(NDIlib_metadata_frame_t) F(NDIlib_metadata_frame_t, length) F(NDIlib_metadata_frame_t, timecode) \
	F(NDIlib_metadata_frame_t, p_data) \
	S(NDIlib_tally_t) F(NDIlib_tally_t, on_program) F(NDIlib_tally_t, on_preview) \
	S(NDIlib_find_create_t) F(NDIlib_find_create_t, show_local_sources) F(NDIlib_find_create_t, p_groups) \
	F(NDIlib_find_create_t, p_extra_ips) \
	S(NDIlib_recv_create_v3_t) F(NDIlib_recv_create_v3_t, source_to_connect_to) \
	F(NDIlib_recv_create_v3_t, color_format) F(NDIlib_recv_create_v3_t, bandwidth) \
	F(NDIlib_recv_create_v3_t, allow_video_fields) F(NDIlib_recv_create_v3_t, p_ndi_recv_name) \
	S(NDIlib_send_create_t) F(NDIlib_send_create_t, p_ndi_name) F(NDIlib_send_create_t, p_groups) \
	F(NDIlib_send_create_t, clock_video) F(NDIlib_send_create_t, clock_audio) \
	S(NDIlib_send_advertiser_create_t) F(NDIlib_send_advertiser_create_t, p_url_address) \
	S(NDIlib_recv_advertiser_create_t) F(NDIlib_recv_advertiser_create_t, p_url_address) \
	S(NDIlib_audio_frame_v3_t)

// Counters read by fake_ndi_stats. Every field is int64; the order is the contract with the tests.
typedef struct fake_ndi_stats_t {
	int64_t initialized;            // 1 between NDIlib_initialize and NDIlib_destroy
	int64_t finds_created;          // NDIlib_find_create_v2 calls
	int64_t sends_created;          // NDIlib_send_create calls (also the next sender's port index)
	int64_t recvs_created;          // NDIlib_recv_create_v3 calls
	int64_t frames_sent;            // non-NULL frames given to send_video_v2 / send_video_async_v2
	int64_t lifetime_violations;    // async buffers changed or released before their sync event
	int64_t send_advertisers;       // live send advertisers
	int64_t recv_advertisers;       // live recv advertisers
	int64_t senders_advertised;     // live (send advertiser, sender) pairs
	int64_t receivers_advertised;   // live (recv advertiser, receiver) pairs
	int64_t last_recv_color_format; // -1 until a receiver is created
	int64_t last_recv_bandwidth;    // -1 until a receiver is created
	int64_t last_send_clock_video;  // 0/1
	int64_t video_captured;         // video frames handed to receivers by recv_capture
} fake_ndi_stats_t;

// Strings read by fake_ndi_strings; each is "" until recorded.
enum {
	FAKE_STR_FIND_GROUPS = 0,
	FAKE_STR_FIND_EXTRA_IPS = 1,
	FAKE_STR_SEND_GROUPS = 2,
	FAKE_STR_DISCOVERY = 3,
	FAKE_STR_SEND_ADVERTISER_URL = 4,
	FAKE_STR_RECV_NAME = 5,
	FAKE_STR_TALLY = 6, // "program,preview", e.g. "1,0"
	FAKE_STR_RECV_ADVERTISER_URL = 7,
	FAKE_STR_RECV_ADVERTISER_GROUP = 8,
	FAKE_STR_COUNT = 9
};

#define FAKE_DEFAULT_VERSION "NDI SDK LINUX 6.3.2 fake"
#define FAKE_HOST "FAKEHOST"
#define FAKE_FIRST_PORT 5960
#define FAKE_CONFIG_MAX_BYTES (1 << 20)

// A frame as held by a source: an owned copy of what was submitted.
typedef struct fake_frame {
	bool present;
	uint64_t seq;
	int xres, yres;
	NDIlib_FourCC_video_type_e fourcc;
	int frame_rate_N, frame_rate_D;
	float picture_aspect_ratio;
	NDIlib_frame_format_type_e frame_format_type;
	int64_t timecode;
	int line_stride;
	uint8_t* data;
	size_t size;
	char* metadata;
	int64_t timestamp;
} fake_frame;

// A source: a sender (handed out as NDIlib_send_instance_t) or an injected source (never handed out).
typedef struct NDIlib_send_instance_type endpoint;
struct NDIlib_send_instance_type {
	endpoint* next;
	uint64_t id;
	bool live; // in the registry; false once reset orphans it
	bool injected;
	char* name;
	char* url;
	NDIlib_source_t desc;
	fake_frame latest;
	NDIlib_tally_t reported_tally;
	bool async_pending;
	const uint8_t* async_data;
	size_t async_size;
	uint64_t async_sum;
};

// A receiver refers to its source by id only, so a destroyed sender can never leave it a dangling pointer.
typedef struct NDIlib_recv_instance_type receiver;
struct NDIlib_recv_instance_type {
	receiver* next;
	uint64_t id;
	bool live;
	char* want_name;
	char* want_url;
	uint64_t source_id; // 0 = not connected
	bool lost;          // the connected source went away and capture has not yet said so
	uint64_t delivered_seq;
	NDIlib_tally_t tally;
};

typedef struct NDIlib_find_instance_type finder;
struct NDIlib_find_instance_type {
	uint64_t* seen_ids; // the source set last returned by get_current_sources
	uint32_t seen_count;
	NDIlib_source_t* sources;
	uint32_t source_count;
};

typedef struct advertiser {
	struct advertiser* next;
	bool live;
	bool of_receivers;
	uint64_t* ids;
	size_t count, capacity;
} advertiser;

static pthread_once_t g_once = PTHREAD_ONCE_INIT;
static pthread_mutex_t g_mu = PTHREAD_MUTEX_INITIALIZER;
static pthread_cond_t g_cv; // CLOCK_MONOTONIC; broadcast on every change any waiter could care about
static endpoint* g_sources;  // registration order
static receiver* g_receivers;
static advertiser* g_advertisers;
static uint64_t g_next_id = 1; // never reset, so ids of orphaned objects never alias new ones
static uint64_t g_frame_seq;
static fake_ndi_stats_t g_stats;
static char* g_strings[FAKE_STR_COUNT];
static char g_version[256];

static void reset_locked(void);
// The frame_format_type fake_ndi_inject_frame gives the frames it injects (progressive until told otherwise; reset restores it).
static int g_inject_format = NDIlib_frame_format_type_progressive;

static void once_init(void) {
	pthread_condattr_t attr;
	pthread_condattr_init(&attr);
	pthread_condattr_setclock(&attr, CLOCK_MONOTONIC);
	pthread_cond_init(&g_cv, &attr);
	pthread_condattr_destroy(&attr);
	reset_locked();
}

static void lock(void) {
	pthread_once(&g_once, once_init);
	pthread_mutex_lock(&g_mu);
}

static void unlock(void) {
	pthread_mutex_unlock(&g_mu);
}

static int64_t now_100ns(void) {
	struct timespec t;
	clock_gettime(CLOCK_MONOTONIC, &t);
	return (int64_t)t.tv_sec * 10000000 + t.tv_nsec / 100;
}

static struct timespec deadline_after(uint32_t timeout_ms) {
	struct timespec t;
	clock_gettime(CLOCK_MONOTONIC, &t);
	t.tv_sec += (time_t)(timeout_ms / 1000u);
	t.tv_nsec += (long)(timeout_ms % 1000u) * 1000000L;
	if (t.tv_nsec >= 1000000000L) {
		t.tv_sec += 1;
		t.tv_nsec -= 1000000000L;
	}
	return t;
}

// False once the deadline has passed.
static bool wait_locked(const struct timespec* deadline) {
	return pthread_cond_timedwait(&g_cv, &g_mu, deadline) != ETIMEDOUT;
}

static char* dup_or_empty(const char* s) {
	return strdup(s ? s : "");
}

static char* dup_nonempty(const char* s) {
	return (s && *s) ? strdup(s) : NULL;
}

__attribute__((format(printf, 1, 2)))
static char* format_dup(const char* fmt, ...) {
	va_list ap;
	va_start(ap, fmt);
	int n = vsnprintf(NULL, 0, fmt, ap);
	va_end(ap);
	if (n < 0)
		return NULL;
	char* s = malloc((size_t)n + 1);
	if (!s)
		return NULL;
	va_start(ap, fmt);
	vsnprintf(s, (size_t)n + 1, fmt, ap);
	va_end(ap);
	return s;
}

static void set_string_locked(int which, const char* value) {
	free(g_strings[which]);
	g_strings[which] = dup_or_empty(value);
}

static uint64_t fnv1a64(const uint8_t* p, size_t n) {
	uint64_t h = 1469598103934665603ULL;
	for (size_t i = 0; i < n; i++) {
		h ^= p[i];
		h *= 1099511628211ULL;
	}
	return h;
}

// Whether every page of [p, p+n) is still mapped, so a released async buffer is reported, not read.
static bool range_mapped(const void* p, size_t n) {
	if (!p || n == 0)
		return true;
	long page_size = sysconf(_SC_PAGESIZE);
	uintptr_t page = page_size > 0 ? (uintptr_t)page_size : 4096u;
	uintptr_t at = (uintptr_t)p & ~(page - 1);
	uintptr_t end = (uintptr_t)p + n;
	unsigned char vec[256];
	while (at < end) {
		uintptr_t chunk = end - at;
		if (chunk > page * sizeof vec)
			chunk = page * sizeof vec;
		if (mincore((void*)at, (size_t)chunk, vec) != 0 && errno == ENOMEM)
			return false;
		at += chunk;
	}
	return true;
}

// Bytes a frame occupies in memory, from the header's description of each FourCC. A zero stride becomes the
// packed stride; an unknown FourCC without a stride has no known size and is carried without data.
static size_t frame_bytes(NDIlib_FourCC_video_type_e fourcc, int xres, int yres, int* stride_io) {
	if (xres <= 0 || yres <= 0)
		return 0;
	int bytes_per_pixel;
	switch (fourcc) {
	case NDIlib_FourCC_video_type_UYVY:
	case NDIlib_FourCC_video_type_UYVA:
	case NDIlib_FourCC_video_type_P216:
	case NDIlib_FourCC_video_type_PA16:
		bytes_per_pixel = 2;
		break;
	case NDIlib_FourCC_video_type_BGRA:
	case NDIlib_FourCC_video_type_BGRX:
		bytes_per_pixel = 4;
		break;
	default:
		bytes_per_pixel = 0;
		break;
	}
	int stride = *stride_io > 0 ? *stride_io : bytes_per_pixel * xres;
	if (stride <= 0)
		return 0;
	*stride_io = stride;
	size_t plane = (size_t)stride * (size_t)yres;
	switch (fourcc) {
	case NDIlib_FourCC_video_type_UYVA: // UYVY plane, then an 8-bit alpha plane
		return plane + (size_t)xres * (size_t)yres;
	case NDIlib_FourCC_video_type_P216: // 16-bit Y plane, then interleaved 16-bit CbCr plane
		return 2 * plane;
	case NDIlib_FourCC_video_type_PA16: // as P216, then a 16-bit alpha plane
		return 3 * plane;
	default:
		return plane;
	}
}

static void frame_clear(fake_frame* f) {
	free(f->data);
	free(f->metadata);
	memset(f, 0, sizeof *f);
}

static void frame_store_locked(fake_frame* f, const NDIlib_video_frame_v2_t* v, int64_t timestamp) {
	frame_clear(f);
	int stride = v->line_stride_in_bytes;
	size_t size = v->p_data ? frame_bytes(v->FourCC, v->xres, v->yres, &stride) : 0;
	f->present = true;
	f->seq = ++g_frame_seq;
	f->xres = v->xres;
	f->yres = v->yres;
	f->fourcc = v->FourCC;
	f->frame_rate_N = v->frame_rate_N;
	f->frame_rate_D = v->frame_rate_D;
	f->picture_aspect_ratio = v->picture_aspect_ratio;
	f->frame_format_type = v->frame_format_type;
	f->timecode = v->timecode;
	f->line_stride = stride;
	if (size > 0) {
		f->data = malloc(size);
		if (f->data) {
			memcpy(f->data, v->p_data, size);
			f->size = size;
		}
	}
	f->metadata = v->p_metadata ? strdup(v->p_metadata) : NULL;
	f->timestamp = timestamp;
}

static bool frame_deliver(const fake_frame* f, NDIlib_video_frame_v2_t* out) {
	uint8_t* data = NULL;
	char* metadata = NULL;
	if (f->size > 0) {
		data = malloc(f->size);
		if (!data)
			return false;
		memcpy(data, f->data, f->size);
	}
	if (f->metadata) {
		metadata = strdup(f->metadata);
		if (!metadata) {
			free(data);
			return false;
		}
	}
	memset(out, 0, sizeof *out);
	out->xres = f->xres;
	out->yres = f->yres;
	out->FourCC = f->fourcc;
	out->frame_rate_N = f->frame_rate_N;
	out->frame_rate_D = f->frame_rate_D;
	out->picture_aspect_ratio = f->picture_aspect_ratio;
	out->frame_format_type = f->frame_format_type;
	out->timecode = f->timecode;
	out->p_data = data;
	out->line_stride_in_bytes = f->line_stride;
	out->p_metadata = metadata;
	out->timestamp = f->timestamp;
	return true;
}

// The sync event of the NDI async contract: the buffer of the previous async send must be unchanged until now.
static void sync_event_locked(endpoint* s) {
	if (!s->async_pending)
		return;
	s->async_pending = false;
	if (!range_mapped(s->async_data, s->async_size) || fnv1a64(s->async_data, s->async_size) != s->async_sum)
		g_stats.lifetime_violations++;
	s->async_data = NULL;
	s->async_size = 0;
}

static endpoint* source_by_id(uint64_t id) {
	for (endpoint* s = g_sources; s; s = s->next)
		if (s->id == id)
			return s;
	return NULL;
}

// A source is named by its full name when one is given, else by its URL.
static endpoint* source_matching(const char* name, const char* url) {
	for (endpoint* s = g_sources; s; s = s->next) {
		if (name && *name) {
			if (strcmp(s->name, name) == 0)
				return s;
		} else if (url && *url && strcmp(s->url, url) == 0) {
			return s;
		}
	}
	return NULL;
}

static void append_source_locked(endpoint* s) {
	endpoint** at = &g_sources;
	while (*at)
		at = &(*at)->next;
	s->next = NULL;
	*at = s;
}

static void unlink_source_locked(endpoint* s) {
	for (endpoint** at = &g_sources; *at; at = &(*at)->next) {
		if (*at == s) {
			*at = s->next;
			return;
		}
	}
}

static void unlink_receiver_locked(receiver* r) {
	for (receiver** at = &g_receivers; *at; at = &(*at)->next) {
		if (*at == r) {
			*at = r->next;
			return;
		}
	}
}

static void unlink_advertiser_locked(advertiser* a) {
	for (advertiser** at = &g_advertisers; *at; at = &(*at)->next) {
		if (*at == a) {
			*at = a->next;
			return;
		}
	}
}

static bool advertiser_remove_id(advertiser* a, uint64_t id) {
	for (size_t i = 0; i < a->count; i++) {
		if (a->ids[i] == id) {
			a->ids[i] = a->ids[--a->count];
			return true;
		}
	}
	return false;
}

static bool advertiser_add_id(advertiser* a, uint64_t id) {
	for (size_t i = 0; i < a->count; i++)
		if (a->ids[i] == id)
			return false;
	if (a->count == a->capacity) {
		size_t capacity = a->capacity ? a->capacity * 2 : 4;
		uint64_t* ids = realloc(a->ids, capacity * sizeof *ids);
		if (!ids)
			return false;
		a->ids = ids;
		a->capacity = capacity;
	}
	a->ids[a->count++] = id;
	return true;
}

// Drops every advertiser entry for this id; the pair counters follow.
static void unadvertise_locked(uint64_t id, bool of_receivers) {
	for (advertiser* a = g_advertisers; a; a = a->next) {
		if (a->of_receivers != of_receivers || !advertiser_remove_id(a, id))
			continue;
		if (of_receivers)
			g_stats.receivers_advertised--;
		else
			g_stats.senders_advertised--;
	}
}

// A receiver whose source vanished is marked lost (capture reports it once); an unconnected receiver connects to
// the first source matching what it asked for, and then sees that source's latest frame as new.
static void resolve_receiver_locked(receiver* r) {
	if (r->source_id && !source_by_id(r->source_id)) {
		r->source_id = 0;
		r->lost = true;
	}
	if (!r->source_id) {
		endpoint* s = source_matching(r->want_name, r->want_url);
		if (s) {
			r->source_id = s->id;
			r->delivered_seq = 0;
		}
	}
}

static void resolve_all_locked(void) {
	for (receiver* r = g_receivers; r; r = r->next)
		resolve_receiver_locked(r);
}

static void endpoint_free(endpoint* s) {
	frame_clear(&s->latest);
	free(s->name);
	free(s->url);
	free(s);
}

static void receiver_free(receiver* r) {
	free(r->want_name);
	free(r->want_url);
	free(r);
}

// Live senders, receivers and advertisers are orphaned, not freed: a test that still holds one may call into it
// or destroy it safely. Injected sources have no outside holder and are freed.
static void reset_locked(void) {
	endpoint* s = g_sources;
	while (s) {
		endpoint* next = s->next;
		s->live = false;
		s->next = NULL;
		if (s->injected)
			endpoint_free(s);
		s = next;
	}
	for (receiver* r = g_receivers; r; r = r->next)
		r->live = false;
	for (advertiser* a = g_advertisers; a; a = a->next)
		a->live = false;
	g_sources = NULL;
	g_receivers = NULL;
	g_advertisers = NULL;
	memset(&g_stats, 0, sizeof g_stats);
	g_inject_format = NDIlib_frame_format_type_progressive;
	g_stats.last_recv_color_format = -1;
	g_stats.last_recv_bandwidth = -1;
	for (int i = 0; i < FAKE_STR_COUNT; i++) {
		free(g_strings[i]);
		g_strings[i] = NULL;
	}
	snprintf(g_version, sizeof g_version, "%s", FAKE_DEFAULT_VERSION);
}

// The string value of "discovery" under "networks" in $NDI_CONFIG_DIR/ndi-config.v1.json, or NULL.
static char* read_config_discovery(void) {
	const char* dir = getenv("NDI_CONFIG_DIR");
	if (!dir || !*dir)
		return NULL;
	char* path = format_dup("%s/ndi-config.v1.json", dir);
	if (!path)
		return NULL;
	FILE* file = fopen(path, "rb");
	free(path);
	if (!file)
		return NULL;
	char* text = malloc(FAKE_CONFIG_MAX_BYTES + 1);
	size_t length = text ? fread(text, 1, FAKE_CONFIG_MAX_BYTES, file) : 0;
	fclose(file);
	if (!text)
		return NULL;
	text[length] = '\0';

	char* result = NULL;
	const char* networks = strstr(text, "\"networks\"");
	const char* key = networks ? strstr(networks, "\"discovery\"") : NULL;
	if (key) {
		const char* p = key + strlen("\"discovery\"");
		while (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n')
			p++;
		if (*p == ':') {
			p++;
			while (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n')
				p++;
			if (*p == '"') {
				p++;
				result = malloc(strlen(p) + 1);
				if (result) {
					size_t n = 0;
					while (*p && *p != '"') {
						if (*p == '\\' && p[1])
							p++;
						result[n++] = *p++;
					}
					result[n] = '\0';
				}
			}
		}
	}
	free(text);
	return result;
}

// ===== NDIlib: library =========================================================================================

FAKE_API bool NDIlib_initialize(void) {
	char* discovery = read_config_discovery();
	lock();
	set_string_locked(FAKE_STR_DISCOVERY, discovery);
	g_stats.initialized = 1;
	unlock();
	free(discovery);
	return true;
}

FAKE_API void NDIlib_destroy(void) {
	lock();
	g_stats.initialized = 0;
	unlock();
}

FAKE_API const char* NDIlib_version(void) {
	lock();
	const char* version = g_version;
	unlock();
	return version;
}

FAKE_API bool NDIlib_is_supported_CPU(void) {
	return true;
}

// ===== NDIlib: find ============================================================================================

static bool finder_set_changed_locked(const finder* f) {
	uint32_t i = 0;
	for (const endpoint* s = g_sources; s; s = s->next, i++)
		if (i >= f->seen_count || f->seen_ids[i] != s->id)
			return true;
	return i != f->seen_count;
}

static void finder_free_sources(finder* f) {
	for (uint32_t i = 0; i < f->source_count; i++) {
		free((void*)f->sources[i].p_ndi_name);
		free((void*)f->sources[i].p_url_address);
	}
	free(f->sources);
	f->sources = NULL;
	f->source_count = 0;
}

FAKE_API NDIlib_find_instance_t NDIlib_find_create_v2(const NDIlib_find_create_t* p_create_settings) {
	finder* f = calloc(1, sizeof *f);
	if (!f)
		return NULL;
	lock();
	set_string_locked(FAKE_STR_FIND_GROUPS, p_create_settings ? p_create_settings->p_groups : NULL);
	set_string_locked(FAKE_STR_FIND_EXTRA_IPS, p_create_settings ? p_create_settings->p_extra_ips : NULL);
	g_stats.finds_created++;
	unlock();
	return f;
}

FAKE_API void NDIlib_find_destroy(NDIlib_find_instance_t p_instance) {
	if (!p_instance)
		return;
	finder_free_sources(p_instance);
	free(p_instance->seen_ids);
	free(p_instance);
}

FAKE_API const NDIlib_source_t* NDIlib_find_get_current_sources(NDIlib_find_instance_t p_instance, uint32_t* p_no_sources) {
	if (p_no_sources)
		*p_no_sources = 0;
	if (!p_instance)
		return NULL;
	finder* f = p_instance;
	lock();
	finder_free_sources(f);
	uint32_t count = 0;
	for (const endpoint* s = g_sources; s; s = s->next)
		count++;
	NDIlib_source_t* sources = count ? calloc(count, sizeof *sources) : NULL;
	uint64_t* ids = count ? calloc(count, sizeof *ids) : NULL;
	if (count && (!sources || !ids)) {
		free(sources);
		free(ids);
		unlock();
		return NULL;
	}
	uint32_t i = 0;
	for (const endpoint* s = g_sources; s; s = s->next, i++) {
		sources[i].p_ndi_name = strdup(s->name);
		sources[i].p_url_address = strdup(s->url);
		ids[i] = s->id;
	}
	free(f->seen_ids);
	f->seen_ids = ids;
	f->seen_count = count;
	f->sources = sources;
	f->source_count = count;
	unlock();
	if (p_no_sources)
		*p_no_sources = count;
	return sources;
}

FAKE_API bool NDIlib_find_wait_for_sources(NDIlib_find_instance_t p_instance, uint32_t timeout_in_ms) {
	if (!p_instance)
		return false;
	struct timespec deadline = deadline_after(timeout_in_ms);
	lock();
	bool changed;
	while (!(changed = finder_set_changed_locked(p_instance)) && wait_locked(&deadline)) {
	}
	unlock();
	return changed;
}

// ===== NDIlib: send ============================================================================================

FAKE_API NDIlib_send_instance_t NDIlib_send_create(const NDIlib_send_create_t* p_create_settings) {
	endpoint* s = calloc(1, sizeof *s);
	if (!s)
		return NULL;
	lock();
	int64_t index = g_stats.sends_created;
	const char* short_name = p_create_settings ? p_create_settings->p_ndi_name : NULL;
	s->name = (short_name && *short_name) ? format_dup(FAKE_HOST " (%s)", short_name)
	                                      : format_dup(FAKE_HOST " (sender-%lld)", (long long)index);
	s->url = format_dup("127.0.0.1:%lld", (long long)(FAKE_FIRST_PORT + index));
	if (!s->name || !s->url) {
		unlock();
		endpoint_free(s);
		return NULL;
	}
	s->id = g_next_id++;
	s->live = true;
	s->desc.p_ndi_name = s->name;
	s->desc.p_url_address = s->url;
	g_stats.sends_created++;
	g_stats.last_send_clock_video = p_create_settings ? (p_create_settings->clock_video ? 1 : 0) : 1;
	set_string_locked(FAKE_STR_SEND_GROUPS, p_create_settings ? p_create_settings->p_groups : NULL);
	append_source_locked(s);
	resolve_all_locked();
	pthread_cond_broadcast(&g_cv);
	unlock();
	return s;
}

FAKE_API void NDIlib_send_destroy(NDIlib_send_instance_t p_instance) {
	if (!p_instance)
		return;
	endpoint* s = p_instance;
	lock();
	sync_event_locked(s);
	if (s->live) {
		unlink_source_locked(s);
		unadvertise_locked(s->id, false);
		resolve_all_locked();
		pthread_cond_broadcast(&g_cv);
	}
	unlock();
	endpoint_free(s);
}

FAKE_API const NDIlib_source_t* NDIlib_send_get_source_name(NDIlib_send_instance_t p_instance) {
	return p_instance ? &p_instance->desc : NULL;
}

FAKE_API void NDIlib_send_send_video_v2(NDIlib_send_instance_t p_instance, const NDIlib_video_frame_v2_t* p_video_data) {
	if (!p_instance)
		return;
	lock();
	sync_event_locked(p_instance);
	if (p_video_data) {
		frame_store_locked(&p_instance->latest, p_video_data, now_100ns());
		g_stats.frames_sent++;
		pthread_cond_broadcast(&g_cv);
	}
	unlock();
}

FAKE_API void NDIlib_send_send_video_async_v2(NDIlib_send_instance_t p_instance, const NDIlib_video_frame_v2_t* p_video_data) {
	if (!p_instance)
		return;
	endpoint* s = p_instance;
	lock();
	sync_event_locked(s);
	if (p_video_data) {
		frame_store_locked(&s->latest, p_video_data, now_100ns());
		g_stats.frames_sent++;
		int stride = p_video_data->line_stride_in_bytes;
		size_t size = p_video_data->p_data
			? frame_bytes(p_video_data->FourCC, p_video_data->xres, p_video_data->yres, &stride) : 0;
		if (size > 0) {
			s->async_pending = true;
			s->async_data = p_video_data->p_data;
			s->async_size = size;
			s->async_sum = fnv1a64(p_video_data->p_data, size);
		}
		pthread_cond_broadcast(&g_cv);
	}
	unlock();
}

// The OR of the tally of every receiver connected to this sender; true when it differs from the last call's.
FAKE_API bool NDIlib_send_get_tally(NDIlib_send_instance_t p_instance, NDIlib_tally_t* p_tally, uint32_t timeout_in_ms) {
	(void)timeout_in_ms;
	if (!p_instance)
		return false;
	lock();
	NDIlib_tally_t tally = { false, false };
	for (const receiver* r = g_receivers; r; r = r->next) {
		if (r->source_id != p_instance->id)
			continue;
		tally.on_program = tally.on_program || r->tally.on_program;
		tally.on_preview = tally.on_preview || r->tally.on_preview;
	}
	bool changed = tally.on_program != p_instance->reported_tally.on_program
		|| tally.on_preview != p_instance->reported_tally.on_preview;
	p_instance->reported_tally = tally;
	unlock();
	if (p_tally)
		*p_tally = tally;
	return changed;
}

FAKE_API int NDIlib_send_get_no_connections(NDIlib_send_instance_t p_instance, uint32_t timeout_in_ms) {
	(void)timeout_in_ms;
	if (!p_instance)
		return 0;
	lock();
	int count = 0;
	for (const receiver* r = g_receivers; r; r = r->next)
		if (r->source_id == p_instance->id)
			count++;
	unlock();
	return count;
}

// ===== NDIlib: recv ============================================================================================

FAKE_API NDIlib_recv_instance_t NDIlib_recv_create_v3(const NDIlib_recv_create_v3_t* p_create_settings) {
	receiver* r = calloc(1, sizeof *r);
	if (!r)
		return NULL;
	if (p_create_settings) {
		r->want_name = dup_nonempty(p_create_settings->source_to_connect_to.p_ndi_name);
		r->want_url = dup_nonempty(p_create_settings->source_to_connect_to.p_url_address);
	}
	lock();
	r->id = g_next_id++;
	r->live = true;
	g_stats.recvs_created++;
	g_stats.last_recv_color_format = p_create_settings ? p_create_settings->color_format : NDIlib_recv_color_format_UYVY_BGRA;
	g_stats.last_recv_bandwidth = p_create_settings ? p_create_settings->bandwidth : NDIlib_recv_bandwidth_highest;
	set_string_locked(FAKE_STR_RECV_NAME, p_create_settings ? p_create_settings->p_ndi_recv_name : NULL);
	r->next = g_receivers;
	g_receivers = r;
	resolve_receiver_locked(r);
	pthread_cond_broadcast(&g_cv);
	unlock();
	return r;
}

FAKE_API void NDIlib_recv_destroy(NDIlib_recv_instance_t p_instance) {
	if (!p_instance)
		return;
	lock();
	if (p_instance->live) {
		unlink_receiver_locked(p_instance);
		unadvertise_locked(p_instance->id, true);
		pthread_cond_broadcast(&g_cv);
	}
	unlock();
	receiver_free(p_instance);
}

FAKE_API void NDIlib_recv_connect(NDIlib_recv_instance_t p_instance, const NDIlib_source_t* p_src) {
	if (!p_instance)
		return;
	char* want_name = p_src ? dup_nonempty(p_src->p_ndi_name) : NULL;
	char* want_url = p_src ? dup_nonempty(p_src->p_url_address) : NULL;
	lock();
	free(p_instance->want_name);
	free(p_instance->want_url);
	p_instance->want_name = want_name;
	p_instance->want_url = want_url;
	p_instance->source_id = 0;
	p_instance->lost = false;
	if (p_instance->live)
		resolve_receiver_locked(p_instance);
	pthread_cond_broadcast(&g_cv);
	unlock();
}

static NDIlib_frame_type_e capture(receiver* r, NDIlib_video_frame_v2_t* video, uint32_t timeout_in_ms) {
	if (!r)
		return NDIlib_frame_type_error;
	struct timespec deadline = deadline_after(timeout_in_ms);
	NDIlib_frame_type_e result = NDIlib_frame_type_none;
	lock();
	for (;;) {
		if (r->live)
			resolve_receiver_locked(r);
		if (r->lost) {
			r->lost = false;
			result = NDIlib_frame_type_error;
			break;
		}
		const endpoint* s = (video && r->source_id) ? source_by_id(r->source_id) : NULL;
		if (s && s->latest.present && s->latest.seq > r->delivered_seq) {
			if (frame_deliver(&s->latest, video)) {
				r->delivered_seq = s->latest.seq;
				g_stats.video_captured++;
				result = NDIlib_frame_type_video;
			}
			break;
		}
		if (!wait_locked(&deadline))
			break;
	}
	unlock();
	return result;
}

FAKE_API NDIlib_frame_type_e NDIlib_recv_capture_v2(
	NDIlib_recv_instance_t p_instance, NDIlib_video_frame_v2_t* p_video_data, NDIlib_audio_frame_v2_t* p_audio_data,
	NDIlib_metadata_frame_t* p_metadata, uint32_t timeout_in_ms) {
	(void)p_audio_data;
	(void)p_metadata;
	return capture(p_instance, p_video_data, timeout_in_ms);
}

FAKE_API NDIlib_frame_type_e NDIlib_recv_capture_v3(
	NDIlib_recv_instance_t p_instance, NDIlib_video_frame_v2_t* p_video_data, NDIlib_audio_frame_v3_t* p_audio_data,
	NDIlib_metadata_frame_t* p_metadata, uint32_t timeout_in_ms) {
	(void)p_audio_data;
	(void)p_metadata;
	return capture(p_instance, p_video_data, timeout_in_ms);
}

FAKE_API void NDIlib_recv_free_video_v2(NDIlib_recv_instance_t p_instance, const NDIlib_video_frame_v2_t* p_video_data) {
	(void)p_instance;
	if (!p_video_data)
		return;
	free(p_video_data->p_data);
	free((void*)p_video_data->p_metadata);
}

FAKE_API void NDIlib_recv_free_audio_v3(NDIlib_recv_instance_t p_instance, const NDIlib_audio_frame_v3_t* p_audio_data) {
	(void)p_instance;
	(void)p_audio_data;
}

FAKE_API void NDIlib_recv_free_metadata(NDIlib_recv_instance_t p_instance, const NDIlib_metadata_frame_t* p_metadata) {
	(void)p_instance;
	if (p_metadata)
		free(p_metadata->p_data);
}

FAKE_API void NDIlib_recv_free_string(NDIlib_recv_instance_t p_instance, const char* p_string) {
	(void)p_instance;
	free((void*)p_string);
}

FAKE_API bool NDIlib_recv_set_tally(NDIlib_recv_instance_t p_instance, const NDIlib_tally_t* p_tally) {
	if (!p_instance)
		return false;
	NDIlib_tally_t tally = p_tally ? *p_tally : (NDIlib_tally_t){ false, false };
	char text[8];
	snprintf(text, sizeof text, "%d,%d", tally.on_program ? 1 : 0, tally.on_preview ? 1 : 0);
	lock();
	p_instance->tally = tally;
	set_string_locked(FAKE_STR_TALLY, text);
	bool connected = p_instance->source_id != 0;
	pthread_cond_broadcast(&g_cv);
	unlock();
	return connected;
}

FAKE_API int NDIlib_recv_get_no_connections(NDIlib_recv_instance_t p_instance) {
	if (!p_instance)
		return 0;
	lock();
	if (p_instance->live)
		resolve_receiver_locked(p_instance);
	int connected = p_instance->source_id ? 1 : 0;
	unlock();
	return connected;
}

// ===== NDIlib: advertisers =====================================================================================

static advertiser* advertiser_create(const char* url, bool of_receivers) {
	advertiser* a = calloc(1, sizeof *a);
	if (!a)
		return NULL;
	lock();
	a->live = true;
	a->of_receivers = of_receivers;
	set_string_locked(of_receivers ? FAKE_STR_RECV_ADVERTISER_URL : FAKE_STR_SEND_ADVERTISER_URL, url);
	if (of_receivers)
		g_stats.recv_advertisers++;
	else
		g_stats.send_advertisers++;
	a->next = g_advertisers;
	g_advertisers = a;
	unlock();
	return a;
}

static void advertiser_destroy(advertiser* a) {
	if (!a)
		return;
	lock();
	if (a->live) {
		unlink_advertiser_locked(a);
		if (a->of_receivers) {
			g_stats.recv_advertisers--;
			g_stats.receivers_advertised -= (int64_t)a->count;
		} else {
			g_stats.send_advertisers--;
			g_stats.senders_advertised -= (int64_t)a->count;
		}
	}
	unlock();
	free(a->ids);
	free(a);
}

static bool advertiser_add(advertiser* a, uint64_t id) {
	lock();
	bool added = advertiser_add_id(a, id);
	if (added && a->live) {
		if (a->of_receivers)
			g_stats.receivers_advertised++;
		else
			g_stats.senders_advertised++;
	}
	unlock();
	return added;
}

static bool advertiser_del(advertiser* a, uint64_t id) {
	lock();
	bool removed = advertiser_remove_id(a, id);
	if (removed && a->live) {
		if (a->of_receivers)
			g_stats.receivers_advertised--;
		else
			g_stats.senders_advertised--;
	}
	unlock();
	return removed;
}

FAKE_API NDIlib_send_advertiser_instance_t NDIlib_send_advertiser_create(const NDIlib_send_advertiser_create_t* p_create_settings) {
	return (NDIlib_send_advertiser_instance_t)(void*)advertiser_create(
		p_create_settings ? p_create_settings->p_url_address : NULL, false);
}

FAKE_API void NDIlib_send_advertiser_destroy(NDIlib_send_advertiser_instance_t p_instance) {
	advertiser_destroy((advertiser*)(void*)p_instance);
}

FAKE_API bool NDIlib_send_advertiser_add_sender(
	NDIlib_send_advertiser_instance_t p_instance, NDIlib_send_instance_t p_sender, bool allow_monitoring) {
	(void)allow_monitoring;
	if (!p_instance || !p_sender)
		return false;
	return advertiser_add((advertiser*)(void*)p_instance, p_sender->id);
}

FAKE_API bool NDIlib_send_advertiser_del_sender(NDIlib_send_advertiser_instance_t p_instance, NDIlib_send_instance_t p_sender) {
	if (!p_instance || !p_sender)
		return false;
	return advertiser_del((advertiser*)(void*)p_instance, p_sender->id);
}

FAKE_API NDIlib_recv_advertiser_instance_t NDIlib_recv_advertiser_create(const NDIlib_recv_advertiser_create_t* p_create_settings) {
	return (NDIlib_recv_advertiser_instance_t)(void*)advertiser_create(
		p_create_settings ? p_create_settings->p_url_address : NULL, true);
}

FAKE_API void NDIlib_recv_advertiser_destroy(NDIlib_recv_advertiser_instance_t p_instance) {
	advertiser_destroy((advertiser*)(void*)p_instance);
}

FAKE_API bool NDIlib_recv_advertiser_add_receiver(
	NDIlib_recv_advertiser_instance_t p_instance, NDIlib_recv_instance_t p_receiver,
	bool allow_controlling, bool allow_monitoring, const char* p_input_group_name) {
	(void)allow_controlling;
	(void)allow_monitoring;
	if (!p_instance || !p_receiver)
		return false;
	lock();
	set_string_locked(FAKE_STR_RECV_ADVERTISER_GROUP, p_input_group_name);
	unlock();
	return advertiser_add((advertiser*)(void*)p_instance, p_receiver->id);
}

FAKE_API bool NDIlib_recv_advertiser_del_receiver(NDIlib_recv_advertiser_instance_t p_instance, NDIlib_recv_instance_t p_receiver) {
	if (!p_instance || !p_receiver)
		return false;
	return advertiser_del((advertiser*)(void*)p_instance, p_receiver->id);
}

// ===== Control exports for the tests ===========================================================================

// NULL restores the default version string.
FAKE_API void fake_ndi_set_version(const char* version) {
	lock();
	snprintf(g_version, sizeof g_version, "%s", version ? version : FAKE_DEFAULT_VERSION);
	unlock();
}

FAKE_API void fake_ndi_reset(void) {
	lock();
	reset_locked();
	pthread_cond_broadcast(&g_cv);
	unlock();
}

// 1 when added; 0 when refused (no name, or a source of that full name is already registered).
FAKE_API int fake_ndi_inject_source(const char* full_name, const char* url) {
	if (!full_name || !*full_name)
		return 0;
	endpoint* s = calloc(1, sizeof *s);
	if (!s)
		return 0;
	s->name = strdup(full_name);
	s->url = dup_or_empty(url);
	if (!s->name || !s->url) {
		endpoint_free(s);
		return 0;
	}
	lock();
	if (source_matching(full_name, NULL)) {
		unlock();
		endpoint_free(s);
		return 0;
	}
	s->id = g_next_id++;
	s->live = true;
	s->injected = true;
	s->desc.p_ndi_name = s->name;
	s->desc.p_url_address = s->url;
	append_source_locked(s);
	resolve_all_locked();
	pthread_cond_broadcast(&g_cv);
	unlock();
	return 1;
}

// A frame from an injected source, delivered to the receivers connected to it. A timestamp <= 0 is stamped with
// the submit time, as a sender's frame is; the timecode equals the timestamp.
FAKE_API bool fake_ndi_inject_frame(const char* full_name, int fourcc, int xres, int yres, const void* data,
	int line_stride, int64_t timestamp_100ns, const char* metadata) {
	if (!full_name)
		return false;
	lock();
	endpoint* s = source_matching(full_name, NULL);
	if (!s || !s->injected) {
		unlock();
		return false;
	}
	int64_t timestamp = timestamp_100ns > 0 ? timestamp_100ns : now_100ns();
	NDIlib_video_frame_v2_t v;
	memset(&v, 0, sizeof v);
	v.xres = xres;
	v.yres = yres;
	v.FourCC = (NDIlib_FourCC_video_type_e)fourcc;
	v.frame_rate_N = 60;
	v.frame_rate_D = 1;
	v.picture_aspect_ratio = 0.0f;
	v.frame_format_type = (NDIlib_frame_format_type_e)g_inject_format;
	v.timecode = timestamp;
	v.p_data = (uint8_t*)(uintptr_t)data;
	v.line_stride_in_bytes = line_stride;
	v.p_metadata = metadata;
	frame_store_locked(&s->latest, &v, timestamp);
	pthread_cond_broadcast(&g_cv);
	unlock();
	return true;
}

// The frame_format_type of the frames injected from now on: 1 progressive, 0 interleaved, 2 field 0, 3 field 1.
FAKE_API void fake_ndi_set_inject_format(int frame_format_type) {
	lock();
	g_inject_format = frame_format_type;
	unlock();
}

FAKE_API void fake_ndi_stats(fake_ndi_stats_t* out) {
	if (!out)
		return;
	lock();
	*out = g_stats;
	unlock();
}

// Copies recorded string `which` (see FAKE_STR_*) into buf, truncated and always terminated; unknown is "".
FAKE_API void fake_ndi_strings(int which, char* buf, int len) {
	if (!buf || len <= 0)
		return;
	lock();
	const char* value = (which >= 0 && which < FAKE_STR_COUNT && g_strings[which]) ? g_strings[which] : "";
	snprintf(buf, (size_t)len, "%s", value);
	unlock();
}

// Writes the first n of the FAKE_NDI_LAYOUT values (sizeof and offsetof of the transcribed structs).
FAKE_API void fake_ndi_layout(int64_t* out, int n) {
	static const int64_t values[] = {
#define FAKE_LAYOUT_SIZE(T) (int64_t)sizeof(T),
#define FAKE_LAYOUT_FIELD(T, f) (int64_t)offsetof(T, f),
		FAKE_NDI_LAYOUT(FAKE_LAYOUT_SIZE, FAKE_LAYOUT_FIELD)
#undef FAKE_LAYOUT_SIZE
#undef FAKE_LAYOUT_FIELD
	};
	if (!out)
		return;
	for (int i = 0; i < n && i < (int)(sizeof values / sizeof values[0]); i++)
		out[i] = values[i];
}
