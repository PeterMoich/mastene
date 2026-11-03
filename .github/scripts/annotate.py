#!/usr/bin/env python3
"""Turn build errors and failed tests into GitHub annotations, so failures show up on the
commit and the PR without anyone opening the raw log.

Usage:
  annotate.py build build.log          # compiler/NuGet errors from a dotnet build log
  annotate.py trx TestResults/         # failed tests from .trx files
"""
import glob
import re
import sys
import xml.etree.ElementTree as ET

MAX = 40


def escape(text: str) -> str:
    return text.replace("%", "%25").replace("\r", "").replace("\n", "%0A")


def build(log_path: str) -> None:
    pattern = re.compile(r": (error|warning) [A-Z]+\d+")
    seen = []
    with open(log_path, encoding="utf-8", errors="replace") as log:
        for line in log:
            if pattern.search(line):
                cleaned = re.sub(r" \[[^\]]*\]$", "", line.strip())
                cleaned = re.sub(r"^.*?/(src|tests)/", r"\1/", cleaned)
                if cleaned not in seen:
                    seen.append(cleaned)
    if seen:
        print(f"::error title=Build failed ({len(seen)} problems)::{escape(chr(10).join(seen[:MAX]))}")


def trx(folder: str) -> None:
    ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    failures = []
    for path in glob.glob(f"{folder}/**/*.trx", recursive=True):
        root = ET.parse(path).getroot()
        for result in root.iterfind(".//t:UnitTestResult", ns):
            if result.get("outcome") != "Failed":
                continue
            message = result.findtext(".//t:Message", default="", namespaces=ns).strip()
            stack = result.findtext(".//t:StackTrace", default="", namespaces=ns).strip()
            first_frames = "\n".join(stack.splitlines()[:4])
            failures.append(f"{result.get('testName')}\n{message[:1500]}\n{first_frames}")
    for failure in failures[:MAX]:
        name = failure.splitlines()[0]
        print(f"::error title=Test failed: {escape(name)[:200]}::{escape(failure)}")
    if failures:
        print(f"{len(failures)} test(s) failed")


if __name__ == "__main__":
    {"build": build, "trx": trx}[sys.argv[1]](sys.argv[2])
