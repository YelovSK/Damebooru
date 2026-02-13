#!/usr/bin/env python3
"""
Migrate tags, tag categories, and sources from Oxibooru to Bakabooru by
reverse-searching Bakabooru posts against Oxibooru.

Flow:
1. Iterate Bakabooru posts (paged).
2. Process only image/gif posts (skip videos).
3. Download post content from Bakabooru.
4. If source is JXL, decode it to JPEG using `djxl`.
5. Reverse-search file in Oxibooru (`/posts/reverse-search`).
6. If exact match exists (or a sufficiently close similar match), sync tag
   categories + tags to Bakabooru.
7. Add missing sources to the Bakabooru post.

Requirements:
- Python 3.10+
- `requests` package
- `djxl` in PATH (only required for JXL inputs)
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import tempfile
from dataclasses import dataclass
from pathlib import Path
from typing import Any

import requests


DEFAULT_OXIBOORU_API = "https://oxibooru.yelov.net/api"
DEFAULT_BAKABOORU_API = "http://localhost:4200/api"


def normalize_name(name: str) -> str:
    return name.strip().lower()


def is_supported_content_type(content_type: str) -> bool:
    ct = (content_type or "").lower()
    if ct.startswith("video/"):
        return False
    return ct.startswith("image/")


def is_jxl_content_type(content_type: str) -> bool:
    ct = (content_type or "").lower()
    return ct in {"image/jxl", "image/jxlp", "image/jxl-sequence"}


def decode_jxl_to_jpeg(jxl_bytes: bytes) -> bytes:
    with tempfile.TemporaryDirectory(prefix="baka-jxl-") as tmpdir:
        in_path = Path(tmpdir) / "input.jxl"
        out_path = Path(tmpdir) / "output.jpg"
        in_path.write_bytes(jxl_bytes)

        result = subprocess.run(
            ["djxl", str(in_path), str(out_path)],
            capture_output=True,
            text=True,
            check=False,
        )
        if result.returncode != 0:
            raise RuntimeError(
                "djxl failed with exit code "
                f"{result.returncode}: {result.stderr.strip() or result.stdout.strip()}"
            )

        if not out_path.exists():
            raise RuntimeError("djxl succeeded but did not produce output file.")

        return out_path.read_bytes()


def with_leading_slash(path: str) -> str:
    if path.startswith("/"):
        return path
    return f"/{path}"


@dataclass
class ManagedTag:
    id: int
    name: str
    category_id: int | None


@dataclass
class ManagedCategory:
    id: int
    name: str
    color: str
    order: int


class BakabooruClient:
    def __init__(
        self,
        api_base: str,
        username: str | None = None,
        password: str | None = None,
        timeout: int = 60,
    ) -> None:
        self.api_base = api_base.rstrip("/")
        self.timeout = timeout
        self.session = requests.Session()
        self.session.headers.update({"Accept": "application/json"})

        if username and password:
            self.login(username, password)

    def _url(self, path: str) -> str:
        return f"{self.api_base}{with_leading_slash(path)}"

    def _raise_for_status(self, response: requests.Response, context: str) -> None:
        if response.ok:
            return

        detail = response.text
        try:
            parsed = response.json()
            if isinstance(parsed, dict):
                detail = parsed.get("description") or parsed.get("title") or json.dumps(parsed)
        except Exception:
            pass

        raise RuntimeError(f"{context} failed: HTTP {response.status_code} - {detail}")

    def login(self, username: str, password: str) -> None:
        response = self.session.post(
            self._url("/auth/login"),
            json={"username": username, "password": password},
            timeout=self.timeout,
        )
        self._raise_for_status(response, "Bakabooru login")

    def get_posts_page(self, page: int, page_size: int) -> dict[str, Any]:
        response = self.session.get(
            self._url("/posts"),
            params={"page": page, "pageSize": page_size},
            timeout=self.timeout,
        )
        self._raise_for_status(response, "Bakabooru list posts")
        data = response.json()
        if not isinstance(data, dict):
            raise RuntimeError("Bakabooru list posts returned unexpected payload.")
        return data

    def get_post_content(self, post_id: int) -> bytes:
        response = self.session.get(
            self._url(f"/posts/{post_id}/content"),
            timeout=self.timeout,
        )
        self._raise_for_status(response, f"Bakabooru fetch content for post {post_id}")
        return response.content

    def get_categories(self) -> list[ManagedCategory]:
        response = self.session.get(self._url("/tagcategories"), timeout=self.timeout)
        self._raise_for_status(response, "Bakabooru list categories")
        payload = response.json()
        if not isinstance(payload, list):
            raise RuntimeError("Bakabooru categories payload is not a list.")

        result: list[ManagedCategory] = []
        for item in payload:
            result.append(
                ManagedCategory(
                    id=int(item["id"]),
                    name=str(item["name"]),
                    color=str(item["color"]),
                    order=int(item.get("order", 0)),
                )
            )
        return result

    def create_category(self, name: str, color: str, order: int) -> ManagedCategory:
        response = self.session.post(
            self._url("/tagcategories"),
            json={"name": name, "color": color, "order": order},
            timeout=self.timeout,
        )
        self._raise_for_status(response, f"Bakabooru create category '{name}'")
        item = response.json()
        return ManagedCategory(
            id=int(item["id"]),
            name=str(item["name"]),
            color=str(item["color"]),
            order=int(item.get("order", 0)),
        )

    def get_all_tags(self) -> list[ManagedTag]:
        page = 1
        page_size = 500
        tags: list[ManagedTag] = []

        while True:
            response = self.session.get(
                self._url("/tags"),
                params={"page": page, "pageSize": page_size},
                timeout=self.timeout,
            )
            self._raise_for_status(response, "Bakabooru list tags")
            payload = response.json()

            items = payload.get("items") or payload.get("Items") or []
            if not isinstance(items, list):
                raise RuntimeError("Bakabooru tags payload has invalid 'items'.")

            for item in items:
                tags.append(
                    ManagedTag(
                        id=int(item["id"]),
                        name=str(item["name"]),
                        category_id=(int(item["categoryId"]) if item.get("categoryId") is not None else None),
                    )
                )

            if len(items) < page_size:
                break
            page += 1

        return tags

    def create_tag(self, name: str, category_id: int | None) -> ManagedTag:
        response = self.session.post(
            self._url("/tags"),
            json={"name": name, "categoryId": category_id},
            timeout=self.timeout,
        )
        self._raise_for_status(response, f"Bakabooru create tag '{name}'")
        payload = response.json()
        return ManagedTag(
            id=int(payload["id"]),
            name=str(payload["name"]),
            category_id=(int(payload["categoryId"]) if payload.get("categoryId") is not None else None),
        )

    def update_tag(self, tag_id: int, name: str, category_id: int | None) -> ManagedTag:
        response = self.session.put(
            self._url(f"/tags/{tag_id}"),
            json={"name": name, "categoryId": category_id},
            timeout=self.timeout,
        )
        self._raise_for_status(response, f"Bakabooru update tag '{name}'")
        payload = response.json()
        return ManagedTag(
            id=int(payload["id"]),
            name=str(payload["name"]),
            category_id=(int(payload["categoryId"]) if payload.get("categoryId") is not None else None),
        )

    def add_tag_to_post(self, post_id: int, tag_name: str) -> tuple[bool, int]:
        response = self.session.post(
            self._url(f"/posts/{post_id}/tags"),
            data=json.dumps(tag_name),
            headers={"Content-Type": "application/json", "Accept": "application/json"},
            timeout=self.timeout,
        )
        if response.status_code in (204, 201, 200):
            return True, response.status_code
        if response.status_code == 409:
            return False, response.status_code

        self._raise_for_status(response, f"Bakabooru add tag '{tag_name}' to post {post_id}")
        return False, response.status_code

    def get_post_sources(self, post_id: int) -> list[str]:
        response = self.session.get(
            self._url(f"/posts/{post_id}/sources"),
            timeout=self.timeout,
        )
        self._raise_for_status(response, f"Bakabooru get sources for post {post_id}")
        payload = response.json()
        if not isinstance(payload, list):
            raise RuntimeError(f"Bakabooru sources payload for post {post_id} is not a list.")
        result: list[str] = []
        for item in payload:
            if isinstance(item, str):
                value = item.strip()
                if value:
                    result.append(value)
        return result

    def set_post_sources(self, post_id: int, sources: list[str]) -> None:
        response = self.session.put(
            self._url(f"/posts/{post_id}/sources"),
            json=sources,
            timeout=self.timeout,
        )
        self._raise_for_status(response, f"Bakabooru set sources for post {post_id}")


class OxibooruClient:
    def __init__(
        self,
        api_base: str,
        token_auth: str | None = None,
        timeout: int = 60,
    ) -> None:
        self.api_base = api_base.rstrip("/")
        self.timeout = timeout
        self.session = requests.Session()
        self.session.headers.update({"Accept": "application/json"})
        if token_auth:
            self.session.headers["Authorization"] = token_auth

    def _url(self, path: str) -> str:
        return f"{self.api_base}{with_leading_slash(path)}"

    def _raise_for_status(self, response: requests.Response, context: str) -> None:
        if response.ok:
            return

        detail = response.text
        try:
            parsed = response.json()
            if isinstance(parsed, dict):
                detail = parsed.get("description") or parsed.get("title") or json.dumps(parsed)
        except Exception:
            pass
        raise RuntimeError(f"{context} failed: HTTP {response.status_code} - {detail}")

    def get_tag_categories(self) -> dict[str, dict[str, Any]]:
        response = self.session.get(self._url("/tag-categories"), timeout=self.timeout)
        self._raise_for_status(response, "Oxibooru list categories")
        payload = response.json()
        results = payload.get("results", [])
        if not isinstance(results, list):
            raise RuntimeError("Oxibooru categories payload has invalid 'results'.")

        mapped: dict[str, dict[str, Any]] = {}
        for item in results:
            name = str(item.get("name", "")).strip()
            if not name:
                continue
            mapped[normalize_name(name)] = {
                "name": name,
                "color": str(item.get("color") or "#808080"),
                "order": int(item.get("order") or 0),
            }
        return mapped

    def reverse_search(
        self,
        content_bytes: bytes,
        filename: str,
        content_type: str,
    ) -> dict[str, Any]:
        files = {
            "content": (filename, content_bytes, content_type),
        }
        response = self.session.post(
            self._url("/posts/reverse-search"),
            files=files,
            timeout=self.timeout,
        )
        self._raise_for_status(response, "Oxibooru reverse search")
        payload = response.json()
        if not isinstance(payload, dict):
            raise RuntimeError("Oxibooru reverse search payload is invalid.")
        return payload


def select_reverse_search_match(
    reverse_result: dict[str, Any],
    max_similar_distance: float,
) -> tuple[dict[str, Any] | None, str, float | None]:
    """
    Pick the most reliable reverse-search candidate.

    Returns: (post, match_kind, distance)
      - match_kind: "exact", "similar", "none", "too_far"
    """
    exact = reverse_result.get("exactPost")
    if isinstance(exact, dict):
        return exact, "exact", 0.0

    similar_posts = reverse_result.get("similarPosts") or []
    if not isinstance(similar_posts, list) or not similar_posts:
        return None, "none", None

    candidates: list[tuple[float, dict[str, Any]]] = []
    for item in similar_posts:
        if not isinstance(item, dict):
            continue
        post = item.get("post")
        if not isinstance(post, dict):
            continue

        distance_raw = item.get("distance")
        try:
            distance = float(distance_raw)
        except (TypeError, ValueError):
            continue

        candidates.append((distance, post))

    if not candidates:
        return None, "none", None

    candidates.sort(key=lambda pair: pair[0])
    best_distance, best_post = candidates[0]
    if best_distance > max_similar_distance:
        return None, "too_far", best_distance

    print("Found non-exact similar match with distance =", best_distance)
    return best_post, "similar", best_distance


class Migrator:
    def __init__(
        self,
        baka: BakabooruClient,
        oxi: OxibooruClient,
        dry_run: bool = False,
    ) -> None:
        self.baka = baka
        self.oxi = oxi
        self.dry_run = dry_run

        self.oxi_categories = self.oxi.get_tag_categories()
        self.categories_by_name: dict[str, ManagedCategory] = {
            normalize_name(c.name): c for c in self.baka.get_categories()
        }
        self.tags_by_name: dict[str, ManagedTag] = {
            normalize_name(t.name): t for t in self.baka.get_all_tags()
        }

    def ensure_category(self, category_name: str | None) -> int | None:
        if not category_name or not category_name.strip():
            return None

        key = normalize_name(category_name)
        existing = self.categories_by_name.get(key)
        if existing:
            return existing.id

        source = self.oxi_categories.get(key, {})
        color = str(source.get("color") or "#808080")
        order = int(source.get("order") or 0)
        display_name = str(source.get("name") or category_name).strip()

        if self.dry_run:
            print(f"[dry-run] create category: name='{display_name}', color='{color}', order={order}")
            return None

        created = self.baka.create_category(display_name, color, order)
        self.categories_by_name[key] = created
        print(f"[category] created '{created.name}' (id={created.id})")
        return created.id

    def ensure_tag(self, tag_name: str, category_id: int | None) -> ManagedTag | None:
        key = normalize_name(tag_name)
        existing = self.tags_by_name.get(key)
        if existing:
            if existing.category_id != category_id:
                if self.dry_run:
                    print(
                        f"[dry-run] update tag category: '{existing.name}' "
                        f"{existing.category_id} -> {category_id}"
                    )
                    return existing

                updated = self.baka.update_tag(existing.id, existing.name, category_id)
                self.tags_by_name[key] = updated
                print(f"[tag] updated '{updated.name}' (id={updated.id}) category={updated.category_id}")
                return updated
            return existing

        if self.dry_run:
            print(f"[dry-run] create tag: '{tag_name}' categoryId={category_id}")
            return None

        created = self.baka.create_tag(tag_name, category_id)
        self.tags_by_name[key] = created
        print(f"[tag] created '{created.name}' (id={created.id}) category={created.category_id}")
        return created

    def migrate_post_tags(
        self,
        post_id: int,
        post_tags: list[dict[str, Any]],
        oxi_tags: list[dict[str, Any]],
    ) -> tuple[int, int]:
        current_post_tags = {
            normalize_name(str(t.get("name", "")))
            for t in post_tags
            if str(t.get("name", "")).strip()
        }

        added_count = 0
        discovered_count = 0
        seen_input_tags: set[str] = set()

        for oxi_tag in oxi_tags:
            names = oxi_tag.get("names") or []
            if not isinstance(names, list) or not names:
                continue

            canonical_name = str(names[0]).strip()
            if not canonical_name:
                continue

            canonical_name = normalize_name(canonical_name)
            if canonical_name in seen_input_tags:
                continue
            seen_input_tags.add(canonical_name)
            discovered_count += 1

            category_name = oxi_tag.get("category")
            category_id = self.ensure_category(str(category_name) if category_name else None)
            self.ensure_tag(canonical_name, category_id)

            if canonical_name in current_post_tags:
                continue

            if self.dry_run:
                print(f"[dry-run] add tag '{canonical_name}' to post {post_id}")
                added_count += 1
                current_post_tags.add(canonical_name)
                continue

            added, status = self.baka.add_tag_to_post(post_id, canonical_name)
            if added:
                print(f"[post:{post_id}] +tag '{canonical_name}'")
                added_count += 1
                current_post_tags.add(canonical_name)
            elif status == 409:
                current_post_tags.add(canonical_name)

        return discovered_count, added_count

    def migrate_post_sources(self, post_id: int, oxi_post: dict[str, Any]) -> tuple[int, int]:
        oxi_sources = extract_oxibooru_sources(oxi_post)
        discovered_count = len(oxi_sources)
        if discovered_count == 0:
            return 0, 0

        current_sources = self.baka.get_post_sources(post_id)
        current_lookup = {s.strip() for s in current_sources}
        to_add = [s for s in oxi_sources if s.strip() not in current_lookup]
        if not to_add:
            return discovered_count, 0

        if self.dry_run:
            for source in to_add:
                print(f"[dry-run] add source to post {post_id}: {source}")
            return discovered_count, len(to_add)

        merged_sources = current_sources + to_add
        self.baka.set_post_sources(post_id, merged_sources)
        for source in to_add:
            print(f"[post:{post_id}] +source '{source}'")
        return discovered_count, len(to_add)


def extract_oxibooru_sources(oxi_post: dict[str, Any]) -> list[str]:
    """
    Oxibooru post has a single `source` field, but we normalize into list and
    support multi-line values.
    """
    raw = oxi_post.get("source")
    if raw is None:
        return []

    if isinstance(raw, str):
        candidates = raw.splitlines()
    elif isinstance(raw, list):
        candidates = [x for x in raw if isinstance(x, str)]
    else:
        return []

    result: list[str] = []
    seen: set[str] = set()
    for item in candidates:
        value = item.strip()
        if not value:
            continue
        key = value.lower()
        if key in seen:
            continue
        seen.add(key)
        result.append(value)
    return result


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Migrate tags/categories from Oxibooru to Bakabooru using reverse image search."
    )
    parser.add_argument("--bakabooru-api", default=DEFAULT_BAKABOORU_API, help="Bakabooru API base URL.")
    parser.add_argument("--oxibooru-api", default=DEFAULT_OXIBOORU_API, help="Oxibooru API base URL.")
    parser.add_argument("--bakabooru-username", default=None, help="Bakabooru username (optional).")
    parser.add_argument("--bakabooru-password", default=None, help="Bakabooru password (optional).")
    parser.add_argument(
        "--oxibooru-auth-header",
        default=None,
        help="Optional Oxibooru Authorization header value (e.g. 'Token <base64>' or 'Basic <base64>').",
    )
    parser.add_argument("--page-size", type=int, default=100, help="Bakabooru posts page size.")
    parser.add_argument("--start-page", type=int, default=1, help="Bakabooru start page.")
    parser.add_argument("--max-posts", type=int, default=0, help="Stop after processing this many posts (0 = no limit).")
    parser.add_argument(
        "--max-similar-distance",
        type=float,
        default=0.05,
        help="Accept similar reverse-search match only when distance is <= this value.",
    )
    parser.add_argument("--dry-run", action="store_true", help="Do not write changes to Bakabooru.")
    parser.add_argument("--fail-fast", action="store_true", help="Abort on first per-post failure.")
    parser.add_argument("--timeout", type=int, default=60, help="HTTP timeout in seconds.")
    return parser.parse_args()


def main() -> int:
    args = parse_args()

    if args.page_size < 1:
        print("Invalid --page-size", file=sys.stderr)
        return 2
    if args.start_page < 1:
        print("Invalid --start-page", file=sys.stderr)
        return 2
    if args.max_similar_distance < 0 or args.max_similar_distance > 1:
        print("Invalid --max-similar-distance (expected 0..1).", file=sys.stderr)
        return 2
    if (args.bakabooru_username and not args.bakabooru_password) or (
        args.bakabooru_password and not args.bakabooru_username
    ):
        print("Both --bakabooru-username and --bakabooru-password are required together.", file=sys.stderr)
        return 2

    baka = BakabooruClient(
        api_base=args.bakabooru_api,
        username=args.bakabooru_username,
        password=args.bakabooru_password,
        timeout=args.timeout,
    )
    oxi = OxibooruClient(
        api_base=args.oxibooru_api,
        token_auth=args.oxibooru_auth_header,
        timeout=args.timeout,
    )
    migrator = Migrator(baka=baka, oxi=oxi, dry_run=args.dry_run)

    processed_total = 0
    scanned_total = 0
    matched_total = 0
    exact_matched_total = 0
    similar_matched_total = 0
    too_far_similar_total = 0
    skipped_type_total = 0
    failed_total = 0
    discovered_tags_total = 0
    added_tags_total = 0
    discovered_sources_total = 0
    added_sources_total = 0

    page = args.start_page
    max_posts = args.max_posts if args.max_posts > 0 else None

    while True:
        page_payload = baka.get_posts_page(page=page, page_size=args.page_size)
        items = page_payload.get("items") or page_payload.get("Items") or []
        if not isinstance(items, list):
            raise RuntimeError("Bakabooru posts payload has invalid 'items'.")
        if not items:
            break

        print(f"[page {page}] fetched {len(items)} posts")

        for post in items:
            if max_posts is not None and scanned_total >= max_posts:
                print("[done] reached --max-posts limit")
                print_summary(
                    scanned_total,
                    processed_total,
                    matched_total,
                    exact_matched_total,
                    similar_matched_total,
                    too_far_similar_total,
                    skipped_type_total,
                    discovered_tags_total,
                    added_tags_total,
                    discovered_sources_total,
                    added_sources_total,
                    failed_total,
                )
                return 0

            scanned_total += 1
            post_id = int(post["id"])
            content_type = str(post.get("contentType") or "")
            relative_path = str(post.get("relativePath") or f"post_{post_id}")
            post_tags = post.get("tags") or []

            if not is_supported_content_type(content_type):
                skipped_type_total += 1
                continue

            processed_total += 1
            try:
                original_bytes = baka.get_post_content(post_id)
                upload_bytes = original_bytes
                upload_mime = content_type if content_type else "application/octet-stream"
                filename = Path(relative_path).name or f"post_{post_id}"

                if is_jxl_content_type(content_type):
                    upload_bytes = decode_jxl_to_jpeg(original_bytes)
                    upload_mime = "image/jpeg"
                    filename = f"{Path(filename).stem}.jpg"

                reverse_result = oxi.reverse_search(
                    content_bytes=upload_bytes,
                    filename=filename,
                    content_type=upload_mime,
                )
                matched_post, match_kind, match_distance = select_reverse_search_match(
                    reverse_result=reverse_result,
                    max_similar_distance=args.max_similar_distance,
                )
                if not matched_post:
                    if match_kind == "too_far":
                        too_far_similar_total += 1
                    continue

                matched_total += 1
                if match_kind == "exact":
                    exact_matched_total += 1
                elif match_kind == "similar":
                    similar_matched_total += 1
                    if match_distance is not None:
                        print(f"[post:{post_id}] using similar match (distance={match_distance:.6f})")

                oxi_tags = matched_post.get("tags") or []
                discovered, added = migrator.migrate_post_tags(
                    post_id=post_id,
                    post_tags=post_tags,
                    oxi_tags=oxi_tags,
                )
                discovered_tags_total += discovered
                added_tags_total += added
                discovered_sources, added_sources = migrator.migrate_post_sources(
                    post_id=post_id,
                    oxi_post=matched_post,
                )
                discovered_sources_total += discovered_sources
                added_sources_total += added_sources

            except Exception as exc:
                failed_total += 1
                print(f"[error] post {post_id}: {exc}", file=sys.stderr)
                if args.fail_fast:
                    print_summary(
                        scanned_total,
                        processed_total,
                        matched_total,
                        exact_matched_total,
                        similar_matched_total,
                        too_far_similar_total,
                        skipped_type_total,
                        discovered_tags_total,
                        added_tags_total,
                        discovered_sources_total,
                        added_sources_total,
                        failed_total,
                    )
                    return 1

        if len(items) < args.page_size:
            break
        page += 1

    print_summary(
        scanned_total,
        processed_total,
        matched_total,
        exact_matched_total,
        similar_matched_total,
        too_far_similar_total,
        skipped_type_total,
        discovered_tags_total,
        added_tags_total,
        discovered_sources_total,
        added_sources_total,
        failed_total,
    )
    return 0


def print_summary(
    scanned_total: int,
    processed_total: int,
    matched_total: int,
    exact_matched_total: int,
    similar_matched_total: int,
    too_far_similar_total: int,
    skipped_type_total: int,
    discovered_tags_total: int,
    added_tags_total: int,
    discovered_sources_total: int,
    added_sources_total: int,
    failed_total: int,
) -> None:
    print("\n=== Migration Summary ===")
    print(f"Scanned posts:          {scanned_total}")
    print(f"Processed image posts:  {processed_total}")
    print(f"Skipped by type:        {skipped_type_total}")
    print(f"Matched posts:          {matched_total}")
    print(f"  exact matches:        {exact_matched_total}")
    print(f"  similar matches:      {similar_matched_total}")
    print(f"  too-far similars:     {too_far_similar_total}")
    print(f"Discovered tags:        {discovered_tags_total}")
    print(f"Added tags to posts:    {added_tags_total}")
    print(f"Discovered sources:     {discovered_sources_total}")
    print(f"Added sources to posts: {added_sources_total}")
    print(f"Failures:               {failed_total}")


if __name__ == "__main__":
    raise SystemExit(main())
