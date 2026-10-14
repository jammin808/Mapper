"""Writes ltc-30fps-44k1.wav: one second of linear timecode, 30 fps non-drop from 10:00:00:00, 44 100 Hz, 16-bit mono PCM.

An independent writer for the decoder's external fixture (no LTC library is used or opened): it lays out each 80-bit word from
the SMPTE ST 12-1 bit table (BCD digits least significant bit first, user bits zero, the polarity bit making the zeros even,
the sync word 0011 1111 1111 1101 in bits 64-79) and writes biphase mark as a plain square wave at half scale: the level flips
at the start of every bit cell and again mid-cell for a 1, each flip at the nearest sample. Run: python3 make_ltc_wav.py
"""
import struct
import wave

RATE = 44100
FPS = 30
AMPLITUDE = 16384


def word(h, m, s, f):
    bits = [0] * 80

    def put(at, width, value):
        for i in range(width):
            bits[at + i] = (value >> i) & 1

    put(0, 4, f % 10)
    put(8, 2, f // 10)
    put(16, 4, s % 10)
    put(24, 3, s // 10)
    put(32, 4, m % 10)
    put(40, 3, m // 10)
    put(48, 4, h % 10)
    put(56, 2, h // 10)
    sync = [0, 0, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 1]
    bits[64:80] = sync
    if bits.count(0) % 2:
        bits[27] = 1  # the polarity correction bit at 30 fps
    return bits


def main():
    samples_per_bit = RATE / (80 * FPS)
    flips = []
    t = 0.0
    for frame in range(FPS):
        for b in word(10, 0, 0, frame):
            flips.append(t)
            if b:
                flips.append(t + samples_per_bit / 2)
            t += samples_per_bit
    flips.append(t)
    total = int(t) + 8
    level = AMPLITUDE
    out = []
    k = 0
    for n in range(total):
        while k < len(flips) and round(flips[k]) <= n:
            level = -level
            k += 1
        out.append(level if k < len(flips) else 0)
    with wave.open("ltc-30fps-44k1.wav", "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(b"".join(struct.pack("<h", v) for v in out))


if __name__ == "__main__":
    main()
