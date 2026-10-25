#!/usr/bin/env python3
"""Build the NativeNCA website into a static folder for GitHub Pages.

    python site/build.py [out_dir]

Environment:
    SITE_URL   Public URL with a trailing slash (GitHub Actions passes the Pages URL).
    GH_TOKEN   Optional. Used to read the latest releases: the download buttons get a
               direct link to the newest zip, and release.json keeps a copy for site.js.
               The Site workflow rebuilds after every tagged release.

Pages: site/index.html (Russian, the default), site/en/index.html, site/kk/index.html.
Each page marks where the icon sprite goes with <!--ICONS-->; the sprite itself is
site/assets/icons.svg, so the three pages share one copy.

Needs Pillow for the WebP screenshots (pip install pillow).
"""
import datetime
import html
import json
import os
import re
import shutil
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "site"
OUT = Path(sys.argv[1] if len(sys.argv) > 1 else ROOT / "_site").resolve()
REPO = os.environ.get("GITHUB_REPOSITORY", "northbound-oss/NativeNCA")
SITE_URL = os.environ.get("SITE_URL") or "https://oss.northbound.sh/NativeNCA/"
if not SITE_URL.endswith("/"):
    SITE_URL += "/"
# configure-pages reports http:// for a custom domain without "Enforce HTTPS". Canonical
# links, hreflang and the sitemap must name the https:// address, or search engines
# index both versions.
if SITE_URL.startswith("http://"):
    SITE_URL = "https://" + SITE_URL[len("http://"):]


def copy_sources():
    if OUT.exists():
        shutil.rmtree(OUT)
    shutil.copytree(SRC, OUT, ignore=shutil.ignore_patterns("build.py", "icons.svg", "__pycache__", ".DS_Store"))
    shutil.copy2(ROOT / "docs" / "icon.png", OUT / "icon.png")
    shutil.copy2(ROOT / "NativeNCA.App" / "Assets" / "app.ico", OUT / "favicon.ico")
    (OUT / ".nojekyll").write_text("")


def screenshots():
    """docs/screenshots/<lang>/*.png (rendered at 2x) -> screenshots/<lang>/*.webp, sized for the web."""
    from PIL import Image

    dst = OUT / "screenshots"
    dst.mkdir(exist_ok=True)
    for lang in ("ru", "en", "kk"):
        lang_dir = ROOT / "docs" / "screenshots" / lang
        if not lang_dir.is_dir():
            continue
        lang_dst = dst / lang
        lang_dst.mkdir(exist_ok=True)
        for png in sorted(lang_dir.glob("*.png")):
            with Image.open(png) as raw:
                im = raw.convert("RGB")
            width = 1600 if im.width > 2000 else 900
            if im.width > width:
                resized = im.resize((width, round(im.height * width / im.width)), Image.LANCZOS)
                im.close()
                im = resized
            webp_name = png.stem + ".webp"
            im.save(lang_dst / webp_name, "WEBP", quality=84, method=5)
            if lang == "ru":
                im.save(dst / webp_name, "WEBP", quality=84, method=5)
            print(f"  {lang}/{png.name} -> {lang}/{webp_name} {im.size[0]}x{im.size[1]}")
            im.close()

    for png in sorted((ROOT / "docs" / "screenshots").glob("*.png")):
        with Image.open(png) as raw:
            im = raw.convert("RGB")
        width = 1600 if im.width > 2000 else 900
        if im.width > width:
            resized = im.resize((width, round(im.height * width / im.width)), Image.LANCZOS)
            im.close()
            im = resized
        im.save(dst / (png.stem + ".webp"), "WEBP", quality=84, method=5)
        print(f"  {png.name} -> {png.stem}.webp {im.size[0]}x{im.size[1]}")
        im.close()


def releases():
    """Bake the latest releases as a fallback for the download button."""
    data = []
    if os.environ.get("GH_TOKEN") and shutil.which("gh"):
        jq = ("[.[] | {tag_name, name, draft, prerelease, html_url, published_at,"
              " assets: [.assets[] | {name, size, browser_download_url}]}]")
        try:
            out = subprocess.run(["gh", "api", f"repos/{REPO}/releases?per_page=10", "--jq", jq],
                                 check=True, capture_output=True, text=True).stdout
            data = json.loads(out)
        except (subprocess.CalledProcessError, json.JSONDecodeError) as e:
            print(f"  could not read releases: {e}", file=sys.stderr)
    (OUT / "release.json").write_text(json.dumps(data, ensure_ascii=False))
    print(f"  release.json: {len(data)} release(s)")
    return data


# Text on the download buttons, per page language.
DL_TEXT = {
    "ru": {"meta": "Версия {v}{beta} · {arch} · {size} МБ", "beta": " (бета)", "dec": ",",
           "none": "Страница релизов на GitHub"},
    "en": {"meta": "Version {v}{beta} · {arch} · {size} MB", "beta": " (beta)", "dec": ".",
           "none": "Releases page on GitHub"},
    "kk": {"meta": "Нұсқа {v}{beta} · {arch} · {size} МБ", "beta": " (бета)", "dec": ",",
           "none": "GitHub-тағы релиздер беті"},
}


def download_link(data, lang="ru"):
    """Direct link to the newest x64 zip, so the button downloads without waiting for JS.

    Same choice as site.js: the newest stable release, a pre-release only if nothing
    stable exists. Without releases the button leads to the Releases page.
    """
    t = DL_TEXT.get(lang, DL_TEXT["ru"])
    ok = [r for r in data if not r.get("draft") and any(a["name"].endswith(".zip") for a in r["assets"])]
    rel = next((r for r in ok if not r.get("prerelease")), ok[0] if ok else None)
    if not rel:
        return f"https://github.com/{REPO}/releases", t["none"]
    asset = next((a for a in rel["assets"] if a["name"].endswith("-win-x64.zip")),
                 next(a for a in rel["assets"] if a["name"].endswith(".zip")))
    size = f"{asset['size'] / 1048576:.1f}".replace(".", t["dec"])
    meta = t["meta"].format(v=rel["tag_name"].lstrip("v"), beta=t["beta"] if rel.get("prerelease") else "",
                            arch="ARM64" if "arm64" in asset["name"] else "x64", size=size)
    return asset["browser_download_url"], meta


def faq_jsonld(page: str) -> str:
    """FAQPage structured data from the <details> expanders, so the FAQ has one source."""
    items = []
    for m in re.finditer(r'<details class="expander">\s*<summary>(.*?)<span class="chev">.*?</summary>\s*'
                         r'<div class="exp-body">(.*?)</div>\s*</details>', page, re.S):
        q = html.unescape(re.sub(r"<[^>]+>", "", m.group(1))).strip()
        a = re.sub(r"\s+", " ", html.unescape(re.sub(r"<[^>]+>", " ", m.group(2)))).strip()
        a = re.sub(r" ([.,:;!?])(?=\s|$)", r"\1", a)
        items.append({"@type": "Question", "name": q, "acceptedAnswer": {"@type": "Answer", "text": a}})
    data = {"@context": "https://schema.org", "@type": "FAQPage", "mainEntity": items}
    return '<script type="application/ld+json">\n' + json.dumps(data, ensure_ascii=False, indent=1) + "\n</script>\n"


def finalize(data):
    today = datetime.date.today().isoformat()
    sprite = (SRC / "assets" / "icons.svg").read_text(encoding="utf-8").strip()
    sprite = "<!-- Fluent UI System Icons (MIT), from site/assets/icons.svg -->\n" + sprite
    for f in OUT.rglob("*"):
        if f.suffix not in {".html", ".xml", ".txt"}:
            continue
        s = f.read_text(encoding="utf-8")
        s = s.replace("__SITE_URL__", SITE_URL).replace("__BUILD_DATE__", today)
        if f.name == "index.html":
            lang = re.search(r'<html lang="([a-z]+)"', s).group(1)
            dl_url, dl_meta = download_link(data, lang)
            s = s.replace("__DOWNLOAD_URL__", html.escape(dl_url)).replace("__DOWNLOAD_META__", html.escape(dl_meta))
            s = s.replace("<!--ICONS-->", sprite, 1)
            s = s.replace("</head>", faq_jsonld(s) + "</head>", 1)
            left = sorted(set(re.findall(r"__[A-Z_]+__|<!--ICONS-->", s)))
            if left:
                sys.exit(f"{f.relative_to(OUT)}: unreplaced placeholders {left}")
        f.write_text(s, encoding="utf-8")


if __name__ == "__main__":
    print(f"Building {SITE_URL} into {OUT}")
    copy_sources()
    screenshots()
    finalize(releases())
    print("Done.")
