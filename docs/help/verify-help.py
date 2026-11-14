"""Check the distributed help's relative links, anchors, assets and screenshot manifest."""

import json
from html.parser import HTMLParser
from pathlib import Path
from urllib.parse import unquote, urlsplit


class Page(HTMLParser):
    def __init__(self, path):
        super().__init__()
        self.path = path
        self.ids = set()
        self.refs = []
        self.images = set()
        self.feed(path.read_text(encoding="utf-8"))
        self.close()

    def handle_starttag(self, tag, attrs):
        values = dict(attrs)
        if "id" in values:
            assert values["id"] not in self.ids, (self.path.name, values["id"])
            self.ids.add(values["id"])
        for name in ("href", "src"):
            if values.get(name):
                self.refs.append(values[name])
        if tag == "img" and values.get("src"):
            assert values.get("alt"), (self.path.name, values)
            self.images.add(values["src"])


root = Path(__file__).resolve().parent
pages = {path: Page(path) for path in root.glob("*.html")}
assert {path.name for path in pages} == {
    "index.html", "quick-start.html", "features.html", "troubleshooting.html",
}
refs = 0
for path, page in pages.items():
    for ref in page.refs:
        target = urlsplit(ref)
        assert not target.scheme and not target.netloc, (path.name, ref)
        resolved = (path.parent / unquote(target.path)).resolve() if target.path else path
        assert resolved.is_relative_to(root) and resolved.is_file(), (path.name, ref)
        if target.fragment:
            assert unquote(target.fragment) in pages[resolved].ids, (path.name, ref)
        refs += 1

manifest = json.loads((root / "screenshots-manifest.json").read_text(encoding="utf-8"))
images = set().union(*(page.images for page in pages.values()))
assert images == {entry["file"] for entry in manifest["captures"]}
assert images == {path.relative_to(root).as_posix() for path in (root / "images").iterdir()}
index_text = (root / "js" / "search-index.js").read_text(encoding="utf-8")
search_index = json.loads(index_text.removeprefix("window.koHelpIndex = ").strip().removesuffix(";"))
for entry in search_index:
    target = urlsplit(entry["href"])
    assert target.fragment in pages[root / target.path].ids, entry["href"]
print(f"PASS: {len(pages)} pages, {refs} references, {len(images)} images, {len(search_index)} search entries")
