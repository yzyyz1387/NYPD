import hashlib
import json
import os
import re
import shutil
from datetime import datetime

import requests


def release_changelog(body):
    lines = []
    for raw in body.splitlines():
        line = raw.strip()
        if not line or line.startswith("#"):
            continue
        line = re.sub(r"^[-*]\s+", "", line)
        line = re.sub(r"^\d+[.)]\s+", "", line)
        if line:
            lines.append(line)
    return lines or ["暂无更新说明"]


def github_release(owner, repo, tag, token):
    response = requests.get(
        f"https://api.github.com/repos/{owner}/{repo}/releases/tags/{tag}",
        headers={"Authorization": f"token {token}", "Accept": "application/vnd.github.v3+json"},
        timeout=20,
    )
    response.raise_for_status()
    return response.json()


def github_asset_url(release, tag):
    name = f"NYPD-{tag}.zip"
    for asset in release["assets"]:
        if asset["name"] == name:
            return asset["browser_download_url"]
    raise RuntimeError(f"未找到发布文件：{name}")


def sha256(url, token):
    response = requests.get(
        url,
        headers={"Authorization": f"token {token}", "Accept": "application/octet-stream"},
        stream=True,
        timeout=60,
    )
    response.raise_for_status()
    digest = hashlib.sha256()
    for chunk in response.iter_content(chunk_size=1024 * 128):
        digest.update(chunk)
    return digest.hexdigest()


def main():
    token = os.environ["GITHUB_TOKEN"]
    owner = os.environ["REPO_OWNER"]
    repo = os.environ["REPO_NAME"]
    tag = os.environ["RELEASE_TAG"]
    version = tag.removeprefix("v").removeprefix("V")

    release = github_release(owner, repo, tag, token)
    changelog = release_changelog(release.get("body") or "")
    github_url = github_asset_url(release, tag)
    gitee_url = f"https://gitee.com/yzyyz1387/NYPD/releases/download/{tag}/NYPD-{tag}.zip"
    if os.path.isdir(".github/pages"):
        shutil.copytree(".github/pages", "dist", dirs_exist_ok=True)
    else:
        os.makedirs("dist", exist_ok=True)
    with open("dist/latest.json", "w", encoding="utf-8") as file:
        json.dump(
            {
                "version": version,
                "releaseDate": datetime.now().strftime("%Y-%m-%d"),
                "downloadUrl": github_url,
                "downloadUrls": {"github": github_url, "gitee": gitee_url},
                "changelog": changelog,
                "sha256": sha256(github_url, token),
            },
            file,
            ensure_ascii=False,
            indent=2,
        )

    with open("dist/CNAME", "w", encoding="utf-8") as file:
        file.write("n.yzyyz.top")


if __name__ == "__main__":
    main()
