#!/usr/bin/env python3
"""Download all available Mixamo animations and write matching CSV metadata.

The S3 URLs Mixamo returns are short-lived, so this script asks Mixamo's API
for a fresh export URL for each motion, then downloads that URL into download/.
"""

from __future__ import annotations

import argparse
import concurrent.futures
import csv
import json
import os
import shutil
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Any


API_ROOT = "https://www.mixamo.com/api/v1"
DEFAULT_STATE = "download/state.json"
DEFAULT_CHARACTER_ID = "48b55a9c-f5ea-4386-9ea7-a3f6036b1529"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Download all available Mixamo animations.")
    parser.add_argument("--csv", default="animations.csv", help="Metadata CSV output path")
    parser.add_argument("--out-dir", default="download", help="Download directory")
    parser.add_argument("--state", default=DEFAULT_STATE, help="Resume state JSON path")
    parser.add_argument(
        "--character-id",
        default=DEFAULT_CHARACTER_ID,
        help=f"Mixamo character ID, default: {DEFAULT_CHARACTER_ID}",
    )
    parser.add_argument(
        "--bearer",
        default=os.environ.get("MIXAMO_BEARER_TOKEN"),
        help="Mixamo bearer token, or set MIXAMO_BEARER_TOKEN",
    )
    parser.add_argument(
        "--bearer-file",
        help="File containing the Mixamo bearer token; overrides --bearer/env",
    )
    parser.add_argument("--format", default="fbx7", help="Mixamo export format")
    parser.add_argument("--skin", default="true", choices=("true", "false"))
    parser.add_argument("--fps", default="30")
    parser.add_argument("--reducekf", default="0")
    parser.add_argument("--inplace", action="store_true", help="Request in-place variant")
    parser.add_argument("--mirror", action="store_true", help="Request mirrored variant")
    parser.add_argument(
        "--download-workers",
        type=int,
        default=4,
        help="Number of simultaneous S3 downloads",
    )
    parser.add_argument("--poll-seconds", type=float, default=2.0)
    parser.add_argument("--timeout-seconds", type=float, default=300.0)
    parser.add_argument("--retries", type=int, default=3)
    parser.add_argument(
        "--metadata-passes",
        type=int,
        default=5,
        help="Maximum full product-list scans to merge unstable Mixamo pages",
    )
    parser.add_argument("--limit", type=int, default=0, help="Stop after N motions, 0 = all")
    parser.add_argument("--dry-run", action="store_true", help="Parse and print work only")
    return parser.parse_args()


def load_bearer(args: argparse.Namespace) -> str:
    if args.bearer_file:
        token = Path(args.bearer_file).read_text().strip()
    else:
        token = (args.bearer or "").strip()
    if token.lower().startswith("bearer "):
        token = token[7:].strip()
    if not token:
        raise SystemExit("Missing Mixamo bearer token. Use --bearer-file or MIXAMO_BEARER_TOKEN.")
    try:
        token.encode("latin-1")
    except UnicodeEncodeError as error:
        raise SystemExit(
            "Mixamo bearer token contains a non-HTTP-header character. "
            "Re-copy the raw token from the browser network request; do not use a truncated value."
        ) from error
    return token


def load_state(path: Path) -> dict[str, Any]:
    if not path.exists():
        return {"completed": {}, "failed": {}}
    with path.open() as f:
        state = json.load(f)
    state.setdefault("completed", {})
    state.setdefault("failed", {})
    return state


def save_state(path: Path, state: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    tmp = path.with_suffix(path.suffix + ".tmp")
    with tmp.open("w") as f:
        json.dump(state, f, indent=2, sort_keys=True)
        f.write("\n")
    tmp.replace(path)


def motion_id(record: dict[str, Any]) -> str:
    return str(record.get("motion_id") or record.get("id") or "")


def output_path(out_dir: Path, record: dict[str, Any]) -> Path:
    return out_dir / f"{motion_id(record)}.fbx"


def is_pack(record: dict[str, Any]) -> bool:
    if record.get("type") == "MotionPack":
        return True
    return " pack" in str(record.get("name") or "").lower()


def unique_by_motion_id(records: list[dict[str, Any]]) -> list[dict[str, Any]]:
    seen: set[str] = set()
    unique_records = []
    for record in records:
        mid = motion_id(record)
        if mid in seen:
            continue
        seen.add(mid)
        unique_records.append(record)
    return unique_records


class MixamoClient:
    def __init__(self, bearer: str, retries: int) -> None:
        self.retries = retries
        self.headers = {
            "Accept": "application/json",
            "Content-Type": "application/json",
            "Authorization": f"Bearer {bearer}",
            "X-Api-Key": "mixamo2",
            "X-Requested-With": "XMLHttpRequest",
            "User-Agent": "mixamo-downloader/1.0",
        }

    def request_json(self, method: str, url: str, body: Any | None = None) -> dict[str, Any]:
        data = None if body is None else json.dumps(body, ensure_ascii=True).encode("ascii")
        last_error: Exception | None = None
        for attempt in range(1, self.retries + 1):
            request = urllib.request.Request(url, data=data, method=method, headers=self.headers)
            try:
                with urllib.request.urlopen(request, timeout=60) as response:
                    raw = response.read().decode()
                    return json.loads(raw) if raw else {}
            except urllib.error.HTTPError as error:
                last_error = error
                if error.code not in (429, 500, 502, 503, 504) or attempt == self.retries:
                    detail = error.read().decode(errors="replace")
                    raise RuntimeError(f"{method} {url} failed: HTTP {error.code}: {detail}") from error
                time.sleep(min(30, 2**attempt))
            except (urllib.error.URLError, TimeoutError) as error:
                last_error = error
                if attempt == self.retries:
                    raise RuntimeError(f"{method} {url} failed: {error}") from error
                time.sleep(min(30, 2**attempt))
        raise RuntimeError(f"{method} {url} failed: {last_error}")

    def product(self, product_id: str, character_id: str) -> dict[str, Any]:
        query = urllib.parse.urlencode({"similar": "0", "character_id": character_id})
        return self.request_json("GET", f"{API_ROOT}/products/{product_id}?{query}")

    def export(self, body: dict[str, Any]) -> dict[str, Any]:
        return self.request_json("POST", f"{API_ROOT}/animations/export", body)

    def monitor(self, character_id: str) -> dict[str, Any]:
        return self.request_json("GET", f"{API_ROOT}/characters/{character_id}/monitor")

    def products_page(self, page: int, limit: int) -> dict[str, Any]:
        query = urllib.parse.urlencode(
            {
                "page": page,
                "limit": limit,
                "order": "",
                "type": "Motion,MotionPack",
                "query": "",
            }
        )
        return self.request_json("GET", f"{API_ROOT}/products?{query}")


def fetch_all_metadata(client: MixamoClient, max_passes: int, limit: int = 100) -> list[dict[str, Any]]:
    records_by_id: dict[str, dict[str, Any]] = {}
    expected_total = 0
    for metadata_pass in range(1, max_passes + 1):
        first_page = client.products_page(1, limit)
        pagination = first_page.get("pagination") or {}
        num_pages = int(first_page.get("num_pages") or pagination.get("num_pages") or 1)
        expected_total = int(first_page.get("num_results") or pagination.get("num_results") or expected_total)
        added = merge_metadata_page(records_by_id, first_page)
        print(
            f"metadata pass {metadata_pass}/{max_passes} page 1/{num_pages}: "
            f"{len(first_page.get('results', []))} records, +{added} new"
        )
        for page in range(2, num_pages + 1):
            data = client.products_page(page, limit)
            added += merge_metadata_page(records_by_id, data)
            print(
                f"metadata pass {metadata_pass}/{max_passes} page {page}/{num_pages}: "
                f"{len(data.get('results', []))} records, total unique={len(records_by_id)}"
            )
        print(
            f"metadata pass {metadata_pass}/{max_passes} complete: "
            f"+{added} new, unique={len(records_by_id)}, expected={expected_total or 'unknown'}"
        )
        if added == 0 or (expected_total and len(records_by_id) >= expected_total):
            break
    return list(records_by_id.values())


def merge_metadata_page(records_by_id: dict[str, dict[str, Any]], data: dict[str, Any]) -> int:
    added = 0
    for record in data.get("results", []):
        if not isinstance(record, dict):
            continue
        product_id = str(record.get("id") or "")
        if not product_id or product_id in records_by_id:
            continue
        records_by_id[product_id] = record
        added += 1
    return added


def normalize_gms_hash(gms_hash: dict[str, Any], inplace: bool, mirror: bool) -> dict[str, Any]:
    result = dict(gms_hash)
    params = result.get("params")
    if isinstance(params, list):
        result["params"] = ",".join(str(item[1] if isinstance(item, list) and len(item) > 1 else item) for item in params)
    if inplace:
        result["inplace"] = True
    if mirror:
        result["mirror"] = True
    return result


def export_url(
    client: MixamoClient,
    record: dict[str, Any],
    args: argparse.Namespace,
) -> str:
    product = client.product(motion_id(record), args.character_id)
    details = product.get("details") or {}
    gms_hash = details.get("gms_hash")
    if not isinstance(gms_hash, dict):
        raise RuntimeError("Product response did not contain details.gms_hash")

    body = {
        "character_id": args.character_id,
        "gms_hash": [normalize_gms_hash(gms_hash, args.inplace, args.mirror)],
        "preferences": {
            "format": args.format,
            "skin": args.skin,
            "fps": args.fps,
            "reducekf": args.reducekf,
        },
        "product_name": record.get("name") or motion_id(record),
        "type": "Motion",
    }
    client.export(body)

    deadline = time.monotonic() + args.timeout_seconds
    while True:
        status = client.monitor(args.character_id)
        state = status.get("status")
        if state == "completed":
            url = status.get("job_result")
            if not isinstance(url, str) or not url:
                raise RuntimeError(f"Export completed without job_result: {status}")
            return url
        if state == "failed":
            raise RuntimeError(f"Export failed: {status}")
        if time.monotonic() >= deadline:
            raise TimeoutError(f"Timed out waiting for export: {status}")
        time.sleep(args.poll_seconds)


def download_file(url: str, destination: Path, retries: int) -> int:
    destination.parent.mkdir(parents=True, exist_ok=True)
    fd, tmp_name = tempfile.mkstemp(prefix=destination.name + ".", suffix=".part", dir=destination.parent)
    os.close(fd)
    tmp = Path(tmp_name)
    try:
        last_error: Exception | None = None
        for attempt in range(1, retries + 1):
            try:
                request = urllib.request.Request(url, headers={"User-Agent": "mixamo-downloader/1.0"})
                with urllib.request.urlopen(request, timeout=120) as response, tmp.open("wb") as f:
                    shutil.copyfileobj(response, f)
                size = tmp.stat().st_size
                if size <= 0:
                    raise RuntimeError("Downloaded file is empty")
                tmp.replace(destination)
                return size
            except Exception as error:  # noqa: BLE001 - report and retry any transfer failure
                last_error = error
                if attempt == retries:
                    raise
                time.sleep(min(30, 2**attempt))
        raise RuntimeError(f"Download failed: {last_error}")
    finally:
        if tmp.exists():
            tmp.unlink()


def write_manifest(out_dir: Path, records: list[dict[str, Any]]) -> None:
    with (out_dir / "manifest.csv").open("w", newline="") as f:
        fieldnames = ["motion_id", "name", "description", "type", "file"]
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        for record in records:
            writer.writerow(
                {
                    "motion_id": motion_id(record),
                    "name": record.get("name") or "",
                    "description": record.get("description") or "",
                    "type": record.get("type") or "",
                    "file": output_path(out_dir, record).as_posix(),
                }
            )


def write_metadata_csv(path: Path, out_dir: Path, records: list[dict[str, Any]]) -> None:
    fieldnames = [
        "id",
        "type",
        "name",
        "description",
        "category",
        "character_type",
        "motion_id",
        "source",
        "thumbnail",
        "thumbnail_animated",
        "motions",
        "matching_file",
        "file_exists",
    ]
    with path.open("w", newline="") as f:
        writer = csv.DictWriter(f, fieldnames=fieldnames)
        writer.writeheader()
        for record in records:
            destination = output_path(out_dir, record)
            writer.writerow(
                {
                    "id": record.get("id") or "",
                    "type": record.get("type") or "",
                    "name": record.get("name") or "",
                    "description": record.get("description") or "",
                    "category": record.get("category") or "",
                    "character_type": record.get("character_type") or "",
                    "motion_id": motion_id(record),
                    "source": record.get("source") or "",
                    "thumbnail": record.get("thumbnail") or "",
                    "thumbnail_animated": record.get("thumbnail_animated") or "",
                    "motions": json.dumps(record.get("motions"), ensure_ascii=False),
                    "matching_file": destination.as_posix(),
                    "file_exists": str(destination.exists() and destination.stat().st_size > 0).lower(),
                }
            )


def main() -> int:
    args = parse_args()
    out_dir = Path(args.out_dir)
    state_path = Path(args.state)
    metadata_csv_path = Path(args.csv)
    bearer = load_bearer(args)
    client = MixamoClient(bearer, args.retries)
    all_records = fetch_all_metadata(client, args.metadata_passes)

    records = unique_by_motion_id(
        [record for record in all_records if motion_id(record) and not is_pack(record)]
    )
    download_records = records[: args.limit] if args.limit else records

    out_dir.mkdir(parents=True, exist_ok=True)
    write_manifest(out_dir, download_records)
    write_metadata_csv(metadata_csv_path, out_dir, all_records)
    state = load_state(state_path)

    pending = []
    for record in download_records:
        mid = motion_id(record)
        destination = output_path(out_dir, record)
        if destination.exists() and destination.stat().st_size > 0:
            state["completed"][mid] = {"file": destination.as_posix(), "size": destination.stat().st_size}
            continue
        if mid in state["completed"]:
            completed_file = Path(state["completed"][mid].get("file", ""))
            if completed_file == destination and completed_file.exists() and completed_file.stat().st_size > 0:
                state["completed"][mid] = {
                    "file": completed_file.as_posix(),
                    "size": completed_file.stat().st_size,
                }
                continue
        pending.append(record)

    save_state(state_path, state)
    print(
        f"records={len(download_records)} metadata={len(all_records)} "
        f"completed={len(download_records) - len(pending)} pending={len(pending)}"
    )
    if args.dry_run or not pending:
        write_metadata_csv(metadata_csv_path, out_dir, all_records)
        return 0

    failures = 0
    downloads: dict[concurrent.futures.Future[int], tuple[str, Path]] = {}

    with concurrent.futures.ThreadPoolExecutor(max_workers=args.download_workers) as pool:
        for index, record in enumerate(pending, 1):
            mid = motion_id(record)
            destination = output_path(out_dir, record)
            try:
                print(f"[{index}/{len(pending)}] exporting {record.get('name') or mid}")
                url = export_url(client, record, args)
                future = pool.submit(download_file, url, destination, args.retries)
                downloads[future] = (mid, destination)
            except Exception as error:  # noqa: BLE001 - keep going and record failure
                failures += 1
                state["failed"][mid] = {"error": str(error), "time": time.time()}
                save_state(state_path, state)
                print(f"failed export {mid}: {error}", file=sys.stderr)

            done = [future for future in downloads if future.done()]
            for future in done:
                done_mid, done_path = downloads.pop(future)
                try:
                    size = future.result()
                    state["completed"][done_mid] = {"file": done_path.as_posix(), "size": size}
                    state["failed"].pop(done_mid, None)
                    print(f"downloaded {done_path} ({size} bytes)")
                except Exception as error:  # noqa: BLE001
                    failures += 1
                    state["failed"][done_mid] = {"error": str(error), "time": time.time()}
                    print(f"failed download {done_mid}: {error}", file=sys.stderr)
                save_state(state_path, state)

        for future in concurrent.futures.as_completed(downloads):
            done_mid, done_path = downloads[future]
            try:
                size = future.result()
                state["completed"][done_mid] = {"file": done_path.as_posix(), "size": size}
                state["failed"].pop(done_mid, None)
                print(f"downloaded {done_path} ({size} bytes)")
            except Exception as error:  # noqa: BLE001
                failures += 1
                state["failed"][done_mid] = {"error": str(error), "time": time.time()}
                print(f"failed download {done_mid}: {error}", file=sys.stderr)
            save_state(state_path, state)

    print(f"complete={len(state['completed'])} failed={len(state['failed'])}")
    write_metadata_csv(metadata_csv_path, out_dir, all_records)
    return 1 if failures else 0


if __name__ == "__main__":
    raise SystemExit(main())
