"""Signs a plugin of HD2 Mod Quick Deploy with the maintainer's key: the manager then shows it as an
official plugin, whatever its version (core/PluginSignature.cs reads the signature).

    python sign_plugin.py HD2QD.Name.dll [...]   sign (again: a signature already there is replaced)
    python sign_plugin.py --verify FILE [...]     whether the file carries a good signature, and whose key
    python sign_plugin.py --public                the key's id and modulus (for PluginSignature.cs)
    python sign_plugin.py --new-key               make the key (refused when one is there)

The private key is a PEM file outside every repository and every backup bundle:
%HD2QD_SIGNING_KEY%, else %USERPROFILE%\\.hd2qd-signing\\plugin_signing_key.pem. Keep a copy in
the password manager. Without it a plugin is left unsigned (said, not an error): someone else's
build of a public plugin is not an official one.

Format, at the file's end: [signature][key id: 8 bytes][signature's length: u32 LE]["HD2QDSG1"].
The signature is RSA PKCS#1 v1.5 with SHA-256 over DOMAIN then the assembly's bytes; the key id is
the first 8 bytes of SHA-256 of the modulus (big-endian)."""
import base64, hashlib, os, struct, sys

from cryptography.exceptions import InvalidSignature
from cryptography.hazmat.primitives import hashes, serialization
from cryptography.hazmat.primitives.asymmetric import padding, rsa

MAGIC = b"HD2QDSG1"
DOMAIN = b"HD2QD plugin signature v1\n"
KEY = os.environ.get("HD2QD_SIGNING_KEY") or os.path.join(os.path.expanduser("~"), ".hd2qd-signing", "plugin_signing_key.pem")


def modulus_of(public):
    n = public.public_numbers().n
    return n.to_bytes((n.bit_length() + 7) // 8, "big")


def key_id(modulus):
    return hashlib.sha256(modulus).digest()[:8]


def split(data):
    """(the assembly, signature, key id), or (data, None, None) when it carries none."""
    if len(data) < len(MAGIC) + 12 or data[-len(MAGIC):] != MAGIC:
        return data, None, None
    (n,) = struct.unpack("<I", data[-len(MAGIC) - 4:-len(MAGIC)])
    end = len(data) - len(MAGIC) - 4 - 8
    if n <= 0 or end - n <= 0:
        return data, None, None
    return data[:end - n], data[end - n:end], data[end:end + 8]


def load_key():
    with open(KEY, "rb") as f:
        return serialization.load_pem_private_key(f.read(), password=None)


def sign(path, key):
    data = open(path, "rb").read()
    body, _, _ = split(data)
    sig = key.sign(DOMAIN + body, padding.PKCS1v15(), hashes.SHA256())
    out = body + sig + key_id(modulus_of(key.public_key())) + struct.pack("<I", len(sig)) + MAGIC
    tmp = path + ".signing"
    with open(tmp, "wb") as f:
        f.write(out)
    os.replace(tmp, path)
    print("signed %s (key %s)" % (path, key_id(modulus_of(key.public_key())).hex()))


def verify(path, public):
    body, sig, kid = split(open(path, "rb").read())
    if sig is None:
        return "unsigned"
    if kid != key_id(modulus_of(public)):
        return "signed by another key (%s)" % kid.hex()
    try:
        public.verify(sig, DOMAIN + body, padding.PKCS1v15(), hashes.SHA256())
        return "signed by the maintainer's key (%s)" % kid.hex()
    except InvalidSignature:
        return "BAD: changed after signing"


def main(args):
    if args[:1] == ["--new-key"]:
        if os.path.exists(KEY):
            sys.exit("a key is already at " + KEY + "; not replaced")
        os.makedirs(os.path.dirname(KEY), exist_ok=True)
        key = rsa.generate_private_key(public_exponent=65537, key_size=3072)
        with open(KEY, "wb") as f:
            f.write(key.private_bytes(serialization.Encoding.PEM, serialization.PrivateFormat.PKCS8, serialization.NoEncryption()))
        print("made " + KEY + " (key " + key_id(modulus_of(key.public_key())).hex() + "); keep a copy in the password manager")
        return
    if not os.path.exists(KEY):
        if args[:1] in (["--public"], ["--verify"]):
            sys.exit("no key at " + KEY)
        for p in args:
            print("NOT SIGNED (no key at %s): %s" % (KEY, p))
        return
    key = load_key()
    if args[:1] == ["--public"]:
        m = modulus_of(key.public_key())
        print("key id  " + key_id(m).hex())
        print("modulus " + base64.b64encode(m).decode())
        return
    if args[:1] == ["--verify"]:
        bad = False
        for p in args[1:]:
            v = verify(p, key.public_key())
            bad |= not v.startswith("signed by the maintainer")
            print("%s: %s" % (p, v))
        sys.exit(1 if bad else 0)
    if not args:
        sys.exit(__doc__)
    for p in args:
        sign(p, key)


if __name__ == "__main__":
    main(sys.argv[1:])
