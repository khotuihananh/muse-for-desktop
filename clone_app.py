import re, shutil, sys
from pathlib import Path

# usage: clone_app.py <target_dir> <Namespace> <Display> <SiteUrl> <OutExe>
target, ns, display, url, outexe = sys.argv[1:6]
src_root = Path.home() / "workspace" / "muse-desktop"
dst = Path(target)
if dst.exists(): shutil.rmtree(dst)
dst.mkdir(parents=True)
for item in ["src", "pack", "tools"]:
    shutil.copytree(src_root / item, dst / item, ignore=shutil.ignore_patterns("bin", "obj"))
for f in ["installer.nsi", "MicrosoftEdgeWebView2Setup.exe"]:
    shutil.copy2(src_root / f, dst / f)
(dst / "src" / "MuseDesktop.csproj").rename(dst / "src" / f"{ns}.csproj")

def patch(path: Path):
    t = path.read_text(encoding="utf-8", errors="ignore")
    orig = t
    t = t.replace("MuseDesktop", ns)
    t = t.replace("Muse Desktop", f"{display} Desktop")
    t = t.replace("MUSE DESKTOP", f"{display.upper()} DESKTOP")
    t = t.replace("https://muse.ai", url)
    t = re.sub(r"\bMuse\b", display, t)
    t = t.replace("1.6.4.0", "1.0.0.0").replace("1.6.4", "1.0.0")
    t = t.replace(f"{ns}-Setup-NSIS.exe", outexe)
    if t != orig: path.write_text(t, encoding="utf-8")

for ext in ("*.cs", "*.xaml", "*.csproj", "*.nsi", "*.txt", "*.manifest"):
    for p in dst.rglob(ext):
        if "bin" not in p.parts and "obj" not in p.parts:
            patch(p)

# Chrome UA: some sign-in flows refuse embedded-webview agents.
mw = dst / "src" / "MainWindow.xaml.cs"
t = mw.read_text(encoding="utf-8")
anchor = "await Web.EnsureCoreWebView2Async(env);"
assert anchor in t
t = t.replace(anchor, anchor + """
            // Present as plain Chrome: some sign-in flows (Google's in
            // particular) can refuse embedded-webview user agents.
            Web.CoreWebView2.Settings.UserAgent =
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/156.0.0.0 Safari/537.36";""", 1)
mw.write_text(t, encoding="utf-8")
print(f"cloned -> {dst} (ns={ns}, display={display}, url={url}, out={outexe})")
