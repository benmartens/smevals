import locale
import os
import sys
from pathlib import Path


def configure_utf8_stdio():
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8")


def decode_output(value):
    if value is None:
        return ""
    if isinstance(value, str):
        decoded = value
    else:
        try:
            decoded = value.decode("utf-8")
        except UnicodeDecodeError as utf8_error:
            if os.name != "nt":
                raise
            encodings = (locale.getpreferredencoding(False), "cp1252")
            for encoding in dict.fromkeys(encodings):
                if encoding.lower().replace("-", "") == "utf8":
                    continue
                try:
                    decoded = value.decode(encoding)
                    break
                except UnicodeDecodeError:
                    pass
            else:
                raise utf8_error
    return decoded.replace("\r\n", "\n").replace("\r", "\n")


def read_text(path):
    return Path(path).read_text(encoding="utf-8")


def write_text(path, value):
    with Path(path).open("w", encoding="utf-8", newline="\n") as fp:
        return fp.write(value)
