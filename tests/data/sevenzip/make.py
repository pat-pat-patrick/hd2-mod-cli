"""Makes the 7z samples SevenZipTests.cs unpacks: one made-up mod in every method py7zr writes,
and expected.txt, each file's path and SHA-256. Run it with py7zr installed (pip install py7zr):

    python make.py

The samples are kept in the repository, so the tests need neither Python nor 7-Zip."""
import hashlib
import os
import random
import shutil
import tempfile

import py7zr

here = os.path.dirname(os.path.abspath(__file__))
src = tempfile.mkdtemp()
thing = os.path.join(src, "Thing")
os.makedirs(os.path.join(thing, "sub dir", "深"))
os.makedirs(os.path.join(thing, "empty dir"))
r = random.Random(7)


def write(rel, data):
    with open(os.path.join(thing, rel), "wb") as f:
        f.write(data)


write("9ba626afa44a3aa3.patch_0", bytes(r.getrandbits(8) for _ in range(6000)))
write("notes.txt", b'{"Version": 1, "Name": "Thing"}\n' * 300)
write(os.path.join("sub dir", "empty.txt"), b"")
write(os.path.join("sub dir", "深", "名字.txt"), "中文 ".encode("utf-8") * 200)
# calls and jumps with near offsets, which the BCJ filter rewrites
code = bytearray()
for _ in range(1500):
    code += bytes(r.getrandbits(8) for _ in range(r.randint(0, 9)))
    code += bytes([r.choice((0xE8, 0xE9))]) + r.getrandbits(16).to_bytes(2, "little") + bytes([0, 0] if r.random() < 0.7 else [0xFF, 0xFF])
write("code.bin", bytes(code))
# runs and noise: long matches and repeated distances
mix = bytearray()
for _ in range(400):
    mix += bytes([r.getrandbits(8)]) * r.randint(1, 200) + bytes(r.getrandbits(8) for _ in range(r.randint(0, 30)))
write("mix.bin", bytes(mix))

variants = {
    "bcj_lzma2": None,  # py7zr's own choice: BCJ + LZMA2
    "lzma": [{"id": py7zr.FILTER_LZMA}],
    "lzma2": [{"id": py7zr.FILTER_LZMA2, "preset": 9}],
    "bcj_lzma": [{"id": py7zr.FILTER_X86}, {"id": py7zr.FILTER_LZMA}],
    "delta_lzma2": [{"id": py7zr.FILTER_DELTA, "dist": 4}, {"id": py7zr.FILTER_LZMA2, "preset": 1}],
    "deflate": [{"id": py7zr.FILTER_DEFLATE}],
    "copy": [{"id": py7zr.FILTER_COPY}],
    "bzip2": [{"id": py7zr.FILTER_BZIP2}],
}
for name, filters in variants.items():
    with py7zr.SevenZipFile(os.path.join(here, name + ".7z"), "w", filters=filters) as z:
        z.writeall(thing, "Thing")
with py7zr.SevenZipFile(os.path.join(here, "password.7z"), "w", password="x") as z:
    z.writeall(thing, "Thing")
with py7zr.SevenZipFile(os.path.join(here, "password_index.7z"), "w", password="x", header_encryption=True) as z:
    z.writeall(thing, "Thing")

lines = []
for d, dirs, files in os.walk(src):
    for x in dirs:
        lines.append(os.path.relpath(os.path.join(d, x), src).replace(os.sep, "/") + "/")
    for x in files:
        p = os.path.join(d, x)
        lines.append(os.path.relpath(p, src).replace(os.sep, "/") + " " + hashlib.sha256(open(p, "rb").read()).hexdigest())
with open(os.path.join(here, "expected.txt"), "w", encoding="utf-8", newline="\n") as f:
    f.write("\n".join(sorted(lines)) + "\n")
shutil.rmtree(src)
print("ok")
