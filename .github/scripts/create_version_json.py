import hashlib
import json
import os
import re
from datetime import datetime

import requests


def parse_version(value):
    main, _, prerelease = value.partition("-")
    numbers = [int(part) for part in main.split(".")]
    match = re.match(r"(alpha|beta|rc)(\d+)", prerelease.lower())
    weights = {"": 3, "rc": 2, "beta": 1, "alpha": 0}
    return numbers, weights.get(match.group(1) if match else "", -1), int(match.group(2)) if match else 0


def parse_version_md():
    with open("version.md", "r", encoding="utf-8") as file:
        blocks = re.split(r"\n##\s+", file.read())

    versions = []
    for block in blocks[1:]:
        lines = block.strip().splitlines()
        version = lines[0].strip() if lines else ""
        changelog = [line.strip()[2:] for line in lines if line.strip().startswith("- ")]
        if version and changelog:
            versions.append((version, changelog))

    versions.sort(key=lambda item: parse_version(item[0]), reverse=True)
    return versions[0] if versions else ("", [])


def github_asset_url(owner, repo, tag, token):
    response = requests.get(
        f"https://api.github.com/repos/{owner}/{repo}/releases/tags/{tag}",
        headers={"Authorization": f"token {token}", "Accept": "application/vnd.github.v3+json"},
        timeout=20,
    )
    response.raise_for_status()
    name = f"NYPD-{tag}.zip"
    for asset in response.json()["assets"]:
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
    version, changelog = parse_version_md()
    if tag != f"v{version}":
        raise RuntimeError(f"Release 标签 {tag} 与 version.md 版本 {version} 不一致，应为 v{version}")

    github_url = github_asset_url(owner, repo, tag, token)
    gitee_url = f"https://gitee.com/yzyyz1387/NYPD/releases/download/{tag}/NYPD-{tag}.zip"
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
