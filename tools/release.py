"""Выпустить новую версию острова для автообновления.

    python tools/release.py 1.0.1 "Что нового — одной строкой"
    python tools/release.py            <- без версии: +1 к последней цифре (кнопка на рабочем столе)

1) ставит <Version> в Island.csproj, 2) собирает Release (dotnet publish),
3) пакует Island.exe + Прочитай.txt в build/Island.zip, 4) считает SHA-256,
5) создаёт черновик релиза v<версия> в публичном репозитории сборок, загружает архив
   (с повторами — 60 МБ через VPN иногда обрываются), проверяет, что файл дошёл целиком,
   и только потом публикует. Пока релиз черновик — друзья его не видят.
Если прошлое нажатие оборвалось и версия осталась черновиком — без аргументов доделываем её, а не прибавляем новую.
Остров у друзей сам увидит новую версию в течение 15 минут.
"""
import hashlib, json, os, re, subprocess, sys, time, zipfile

REPO = "trualontuu/island-releases"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LOG = os.path.join(ROOT, "build", "release.log")


def log(msg):
    """И на экран, и в build/release.log — окно кнопки закрывается, а журнал остаётся."""
    print(msg, flush=True)
    os.makedirs(os.path.dirname(LOG), exist_ok=True)
    with open(LOG, "a", encoding="utf-8") as f:
        f.write(time.strftime("%H:%M:%S ") + msg + "\n")


def gh(*args, check=True):
    r = subprocess.run(["gh", *args], capture_output=True, text=True, encoding="utf-8", errors="replace")
    if r.returncode != 0:
        log(f"gh {' '.join(a for a in args[:3])} → код {r.returncode}: {(r.stderr or r.stdout).strip()[-800:]}")
        if check:
            sys.exit(f"Ошибка GitHub (подробности в build/release.log)")
    return r


def release(tag):
    """Релиз по тегу (в т.ч. черновик) или None."""
    # напрямую, не через gh(): «релиза нет» — обычный ответ, в журнал как ошибку не пишем
    r = subprocess.run(["gh", "release", "view", tag, "--repo", REPO, "--json", "isDraft,assets"], capture_output=True, text=True, encoding="utf-8", errors="replace")
    return json.loads(r.stdout) if r.returncode == 0 else None


def main():
    proj = os.path.join(ROOT, "Island.csproj")
    s = open(proj, encoding="utf-8-sig").read()
    cur = re.search(r"<Version>(\d+)\.(\d+)\.(\d+)</Version>", s)
    if len(sys.argv) >= 2 and re.fullmatch(r"\d+\.\d+\.\d+", sys.argv[1]):
        ver, notes = sys.argv[1], (sys.argv[2] if len(sys.argv) > 2 else "")
    else:
        notes = sys.argv[1] if len(sys.argv) >= 2 else ""
        last = f"{cur[1]}.{cur[2]}.{cur[3]}"
        r = release(f"v{last}")
        if r is None or r["isDraft"]:
            # прошлое нажатие оборвалось (не собралось или не загрузилось) — доделываем ту же версию, номера не пропускаем
            ver = last
            log(f"Версия {last} ещё не вышла — выпускаю её.")
        else:
            ver = f"{cur[1]}.{cur[2]}.{int(cur[3]) + 1}"
    s = re.sub(r"<Version>[^<]*</Version>", f"<Version>{ver}</Version>", s, count=1)
    open(proj, "w", encoding="utf-8-sig").write(s)

    dotnet = os.path.join(os.environ["LOCALAPPDATA"], "Microsoft", "dotnet", "dotnet.exe")
    log(f"=== выпуск {ver}: сборка")
    b = subprocess.run([dotnet, "publish", "-c", "Release", "-o", "dist", "-nologo"], cwd=ROOT, capture_output=True, text=True, encoding="utf-8", errors="replace")
    if b.returncode != 0:
        log("Сборка не удалась:\n" + "\n".join(l for l in b.stdout.splitlines() if "error" in l.lower())[-1500:])
        sys.exit("Сборка не удалась (подробности в build/release.log). Возможно, Island.exe в папке dist занят — нажми ещё раз.")

    os.makedirs(os.path.join(ROOT, "build"), exist_ok=True)
    z = os.path.join(ROOT, "build", "Island.zip")
    with zipfile.ZipFile(z, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as f:
        f.write(os.path.join(ROOT, "dist", "Island.exe"), "Island.exe")
        f.write(os.path.join(ROOT, "docs", "Прочитай.txt"), "Прочитай.txt")
    publish(ver, z, notes)


def publish(ver, z, notes=""):
    tag = f"v{ver}"
    h = hashlib.sha256(open(z, "rb").read()).hexdigest()
    size = os.path.getsize(z)
    body = (notes + "\n\n" if notes else "") + f"sha256: {h}"

    r = release(tag)
    if r is None:
        gh("release", "create", tag, "--repo", REPO, "--title", f"Island {ver}", "--notes", body, "--draft")
    elif not r["isDraft"]:
        sys.exit(f"Версия {ver} уже опубликована. Запусти без аргументов — будет следующая.")
    else:
        gh("release", "edit", tag, "--repo", REPO, "--notes", body)

    for attempt in range(1, 6):
        log(f"Загружаю архив ({size // 1024 // 1024} МБ), попытка {attempt}…")
        t0 = time.time()
        up = gh("release", "upload", tag, z, "--repo", REPO, "--clobber", check=False)
        a = next((a for a in (release(tag) or {}).get("assets", []) if a["name"] == "Island.zip"), None)
        if up.returncode == 0 and a and a["size"] == size:
            log(f"  загружено за {time.time() - t0:.0f} с")
            break
        log(f"  не дошло за {time.time() - t0:.0f} с; на GitHub: {a['size'] if a else 'нет файла'} из {size}")
        time.sleep(5 * attempt)
    else:
        log("Архив так и не загрузился.")
        sys.exit("Архив так и не загрузился — проверь интернет и нажми кнопку ещё раз (эта же версия доделается).")

    gh("release", "edit", tag, "--repo", REPO, "--draft=false", "--latest")
    r = release(tag)
    if not r or r["isDraft"]:
        sys.exit("Архив загружен, но опубликовать не вышло — нажми ещё раз (подробности в build/release.log).")
    log(f"Опубликовано: v{ver}  sha256={h}  {size // 1024 // 1024} МБ")


if __name__ == "__main__":
    # python tools/release.py --upload-only 1.0.5 — догрузить уже собранный build/Island.zip
    if len(sys.argv) >= 3 and sys.argv[1] == "--upload-only":
        publish(sys.argv[2], os.path.join(ROOT, "build", "Island.zip"))
    else:
        main()
