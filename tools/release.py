"""Выпустить новую версию острова для автообновления.

    python tools/release.py 1.0.1 "Что нового — одной строкой"
    python tools/release.py            <- без версии: +1 к последней цифре (кнопка на рабочем столе)

1) ставит <Version> в Island.csproj, 2) собирает Release (dotnet publish),
3) пакует Island.exe + Прочитай.txt в build/Island.zip, 4) считает SHA-256,
5) создаёт релиз v<версия> в публичном репозитории сборок (нужен gh, вход в GitHub).
Остров у друзей сам увидит новую версию в течение 15 минут.
"""
import hashlib, os, re, subprocess, sys, zipfile

REPO = "trualontuu/island-releases"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

def main():
    proj = os.path.join(ROOT, "Island.csproj")
    s = open(proj, encoding="utf-8-sig").read()
    if len(sys.argv) >= 2 and re.fullmatch(r"\d+\.\d+\.\d+", sys.argv[1]):
        ver, notes = sys.argv[1], (sys.argv[2] if len(sys.argv) > 2 else "")
    else:
        cur = re.search(r"<Version>(\d+)\.(\d+)\.(\d+)</Version>", s)
        ver = f"{cur[1]}.{cur[2]}.{int(cur[3]) + 1}"
        notes = sys.argv[1] if len(sys.argv) >= 2 else ""
    s = re.sub(r"<Version>[^<]*</Version>", f"<Version>{ver}</Version>", s, count=1)
    open(proj, "w", encoding="utf-8-sig").write(s)

    dotnet = os.path.join(os.environ["LOCALAPPDATA"], "Microsoft", "dotnet", "dotnet.exe")
    subprocess.run([dotnet, "publish", "-c", "Release", "-o", "dist", "-nologo"], cwd=ROOT, check=True)

    os.makedirs(os.path.join(ROOT, "build"), exist_ok=True)
    z = os.path.join(ROOT, "build", "Island.zip")
    with zipfile.ZipFile(z, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as f:
        f.write(os.path.join(ROOT, "dist", "Island.exe"), "Island.exe")
        f.write(os.path.join(ROOT, "docs", "Прочитай.txt"), "Прочитай.txt")
    h = hashlib.sha256(open(z, "rb").read()).hexdigest()
    body = (notes + "\n\n" if notes else "") + f"sha256: {h}"
    subprocess.run(["gh", "release", "create", f"v{ver}", z, "--repo", REPO, "--title", f"Island {ver}", "--notes", body], check=True)
    print(f"released v{ver}  sha256={h}  size={os.path.getsize(z)//1024//1024} MB")

if __name__ == "__main__":
    main()
