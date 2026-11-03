"""lzma.zip: a mod zip whose entries are packed with LZMA (zip method 14), as HD2 Clear Sight 0.0.5
on Nexus Mods is (2026-11-03), which .NET's zip reader refuses. expected.txt lists every file and
its SHA-256, as SevenZipTests' Listing does. Run it from this folder to make them again."""
import hashlib, os, random, zipfile

r = random.Random(1)

files = {
    "manifest.json": b'{"Version": 1, "Name": "LZMA Sample", "Description": "packed with LZMA"}',
    "Base/9ba626afa44a3aa3.patch_0": bytes(r.randrange(4) for _ in range(300000)),
    "Base/9ba626afa44a3aa3.patch_0.gpu_resources": b"",
    "Base/9ba626afa44a3aa3.patch_0.stream": b"stream " * 4000,
    "Option/50/9ba626afa44a3aa3.patch_0": bytes(range(256)) * 300,
}
with zipfile.ZipFile("lzma.zip", "w", zipfile.ZIP_LZMA) as z:
    for name, data in files.items():
        z.writestr(name, data)
lines = sorted([n + " " + hashlib.sha256(d).hexdigest() for n, d in files.items()] + ["Base/", "Option/", "Option/50/"])
with open("expected.txt", "w", newline="\n") as f:
    f.write("\n".join(lines) + "\n")
print(os.path.getsize("lzma.zip"), "bytes")
