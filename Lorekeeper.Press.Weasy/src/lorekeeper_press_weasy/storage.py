"""Fail-closed immutable artifact publication."""

from __future__ import annotations

import os
from pathlib import Path
import tempfile


def publish_job(output_root: Path, job_id: str, artifacts: dict[str, bytes]) -> Path:
    output_root.mkdir(parents=True, exist_ok=True)
    canonical_root = output_root.resolve(strict=True)
    job_directory = canonical_root / job_id
    lock_path = canonical_root / f".{job_id}.lock"

    try:
        lock_stream = lock_path.open("xb")
    except FileExistsError as exception:
        raise ValueError(f"The immutable job ID is already reserved: {job_id}.") from exception

    staging_directory: Path | None = None
    try:
        lock_stream.close()
        if job_directory.exists():
            raise ValueError(f"The immutable job directory already exists: {job_id}.")

        staging_directory = Path(tempfile.mkdtemp(prefix=f".{job_id}-", dir=canonical_root))
        if staging_directory.resolve(strict=True).parent != canonical_root:
            raise ValueError("The staging directory escaped the approved output root.")
        for name, data in artifacts.items():
            path = staging_directory / name
            with path.open("xb") as stream:
                stream.write(data)
                stream.flush()
                os.fsync(stream.fileno())

        staging_directory.rename(job_directory)
        staging_directory = None
        return job_directory
    finally:
        if staging_directory is not None and staging_directory.exists():
            for path in staging_directory.iterdir():
                if path.is_file():
                    path.unlink()
            staging_directory.rmdir()
        lock_path.unlink(missing_ok=True)
