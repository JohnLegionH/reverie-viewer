# Copyright (c) 2026 Legion Builds
# SPDX-License-Identifier: LGPL-2.1-or-later

"""Turns the virtual display's screen dump (XWD, as Xvfb -fbdir writes it) into a PNG, with the standard library only.

Usage: xwd-to-png.py <in.xwd> <out.png>
Exit 0 when the screen shows a picture, 1 when it is blank (fewer than 16 colours), 2 when the file cannot be read.
Reads the XWD header as the X Window System's XWDFile.h lays it out: 25 big-endian 32-bit fields, the window name,
the colour map (12 bytes per entry), then the pixels in Z format with the header's byte order and colour masks.
"""
import struct
import sys
import zlib


def read_xwd(data):
    f = struct.unpack(">25I", data[:100])
    header_size, depth, width, height = f[0], f[3], f[4], f[5]
    byte_order, bits_per_pixel, bytes_per_line = f[7], f[11], f[12]
    masks = f[14:17]
    ncolors = f[19]
    if f[2] != 2 or bits_per_pixel not in (24, 32):
        raise ValueError(f"unsupported XWD: format {f[2]}, depth {depth}, {bits_per_pixel} bits per pixel")
    start = header_size + ncolors * 12
    step = bits_per_pixel // 8
    shifts = [(m & -m).bit_length() - 1 for m in masks]
    endian = "little" if byte_order == 0 else "big"
    rows = []
    for y in range(height):
        line = data[start + y * bytes_per_line: start + y * bytes_per_line + width * step]
        row = bytearray()
        for x in range(0, len(line), step):
            p = int.from_bytes(line[x:x + step], endian)
            row += bytes(((p & m) >> s) & 0xFF for m, s in zip(masks, shifts))
        rows.append(bytes(row))
    return width, height, rows


def write_png(path, width, height, rows):
    def chunk(kind, body):
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body) & 0xFFFFFFFF)
    raw = b"".join(b"\x00" + r for r in rows)
    with open(path, "wb") as out:
        out.write(b"\x89PNG\r\n\x1a\n")
        out.write(chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0)))
        out.write(chunk(b"IDAT", zlib.compress(raw, 6)))
        out.write(chunk(b"IEND", b""))


def main():
    if len(sys.argv) != 3:
        print(__doc__)
        return 2
    try:
        with open(sys.argv[1], "rb") as f:
            width, height, rows = read_xwd(f.read())
    except (OSError, ValueError, struct.error) as e:
        print(f"xwd-to-png: cannot read {sys.argv[1]}: {e}")
        return 2
    write_png(sys.argv[2], width, height, rows)
    colours = set()
    for r in rows[::4]:
        for x in range(0, len(r), 12):
            colours.add(r[x:x + 3])
    print(f"xwd-to-png: {width}x{height}, {len(colours)} colours in a sample of the screen")
    return 0 if len(colours) >= 16 else 1


if __name__ == "__main__":
    sys.exit(main())
