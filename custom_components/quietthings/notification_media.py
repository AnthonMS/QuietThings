"""Prepare bounded images in HA and keep URLs/credentials away from the TV."""
from __future__ import annotations

import base64
from io import BytesIO
from pathlib import Path
from urllib.parse import unquote, urljoin, urlsplit

import aiohttp
from PIL import Image, ImageOps

from homeassistant.exceptions import HomeAssistantError
from homeassistant.helpers.aiohttp_client import async_get_clientsession

MAX_DOWNLOAD = 8 * 1024 * 1024
MAX_GIF = 1024 * 1024


def prepare_image(content: bytes, animated: bool = True) -> str:
    """Convert common Pillow-readable formats; preserve bounded animations as GIF."""
    if len(content) > MAX_DOWNLOAD:
        raise HomeAssistantError("Notification image exceeds 8 MiB")
    try:
        with Image.open(BytesIO(content)) as original:
            if original.width * original.height > 20_000_000:
                raise ValueError("image dimensions")
            if animated and original.format in {"GIF", "WEBP", "PNG"} and getattr(original, "is_animated", False):
                count = original.n_frames
                if count > 180:
                    raise HomeAssistantError("Animation exceeds 180 frames")
                frames, durations = [], []
                for number in range(count):
                    original.seek(number)
                    frame = original.convert("RGB")
                    frame.thumbnail((520, 285))
                    frames.append(frame)
                    durations.append(max(20, int(original.info.get("duration", 100))))
                for size, colors in (((520, 285), 128), ((360, 200), 64), ((260, 145), 32)):
                    reduced = []
                    for frame in frames:
                        frame = frame.copy()
                        frame.thumbnail(size)
                        reduced.append(frame.quantize(colors=colors))
                    output = BytesIO()
                    reduced[0].save(output, format="GIF", save_all=True, append_images=reduced[1:], duration=durations, loop=0, optimize=True, disposal=2)
                    if output.tell() <= MAX_GIF:
                        return base64.b64encode(output.getvalue()).decode("ascii")
                raise HomeAssistantError("Animation exceeds 1 MiB after resizing")
            image = ImageOps.exif_transpose(original).convert("RGB")
            image.thumbnail((960, 540))
            for quality in (80, 65, 45):
                output = BytesIO()
                image.save(output, format="JPEG", quality=quality)
                if output.tell() <= 131072:
                    return base64.b64encode(output.getvalue()).decode("ascii")
    except (OSError, ValueError, Image.DecompressionBombError):
        raise HomeAssistantError("Notification image could not be decoded") from None
    raise HomeAssistantError("Notification image is too large after resizing")


def _local_image(hass, path):
    root = Path(hass.config.path("www")).resolve()
    image = (root / unquote(path[len("/local/"):])).resolve()
    if not image.is_relative_to(root) or not image.is_file() or image.stat().st_size > MAX_DOWNLOAD:
        raise HomeAssistantError("Local image is missing, outside www, or too large")
    with image.open("rb") as source:
        result = source.read(MAX_DOWNLOAD + 1)
    if len(result) > MAX_DOWNLOAD:
        raise HomeAssistantError("Notification image exceeds 8 MiB")
    return result


def image_url(value):
    """Allow explicit HTTP(S) and HA /local paths; never filesystem URL schemes."""
    if not isinstance(value, str) or len(value) > 2048 or any(ord(c) < 32 for c in value):
        raise ValueError("Invalid image URL")
    parsed = urlsplit(value)
    if parsed.username or parsed.password:
        raise ValueError("Use an image URL without embedded account credentials")
    if not parsed.scheme and not parsed.netloc and parsed.path.startswith("/local/"):
        return value
    if parsed.scheme not in {"http", "https"} or not parsed.hostname:
        raise ValueError("Use an HTTP(S) image URL or /local/path")
    return value


async def fetch_image(hass, url):
    """Fetch in HA, bound redirects/body, never attach the HA access token."""
    image_url(url)
    if url.startswith("/local/"):
        return await hass.async_add_executor_job(_local_image, hass, urlsplit(url).path)
    try:
        session = async_get_clientsession(hass)
        for _ in range(4):
            async with session.get(url, allow_redirects=False, timeout=aiohttp.ClientTimeout(total=10)) as response:
                if response.status in {301, 302, 303, 307, 308}:
                    url = image_url(urljoin(url, response.headers.get("Location", "")))
                    continue
                if response.status != 200:
                    raise HomeAssistantError(f"Image server returned HTTP {response.status}")
                result = bytearray()
                async for chunk in response.content.iter_chunked(65536):
                    result.extend(chunk)
                    if len(result) > MAX_DOWNLOAD:
                        raise HomeAssistantError("Notification image exceeds 8 MiB")
                return bytes(result)
    except (aiohttp.ClientError, TimeoutError, ValueError):
        raise HomeAssistantError("Notification image URL could not be fetched") from None
    raise HomeAssistantError("Image URL has too many redirects")
