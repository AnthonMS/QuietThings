"""Exercise the actual samsungtvws client against an ephemeral loopback TV stub."""

import asyncio
import importlib.metadata
import json

from samsungtvws.async_remote import SamsungTVWSAsyncRemote
from samsungtvws.remote import SendRemoteKey
from websockets.asyncio.server import serve


async def main():
    observed = asyncio.Future()

    async def television(socket):
        await socket.send(
            json.dumps(
                {
                    "event": "ms.channel.connect",
                    "data": {"token": "loopback-test-token", "clients": []},
                }
            )
        )
        packet = json.loads(await socket.recv())
        if not observed.done():
            observed.set_result(packet)
        await socket.wait_closed()

    async with asyncio.timeout(8):
        async with serve(television, "127.0.0.1", 0) as server:
            port = server.sockets[0].getsockname()[1]
            remote = SamsungTVWSAsyncRemote(
                "127.0.0.1", port=port, token="loopback-test-token", timeout=3
            )
            try:
                await remote.open()
                await remote.send_command(SendRemoteKey.click("KEY_POWER"))
                packet = await observed
                assert packet["method"] == "ms.remote.control"
                assert packet["params"]["DataOfCmd"] == "KEY_POWER"
                assert packet["params"]["Cmd"] == "Click"
                print(
                    "Real samsungtvws",
                    importlib.metadata.version("samsungtvws"),
                    "loopback handshake/key passed",
                )
            finally:
                await remote.close()


if __name__ == "__main__":
    asyncio.run(main())
