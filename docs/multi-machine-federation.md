# Multi-Machine Federation

Rover Background Manager supports connecting multiple machines together so that a single MCP client can discover and control apps running across all of them. You point your AI tool (such as Copilot) at one machine — the **root** — and it sees apps on every other machine that has been linked to it.

---

## How It Works

Each machine runs its own Background Manager. Normally a manager only listens on `localhost` and is not reachable over the network. Federation works by:

1. **Machine B** opts in to external access, which opens a network listener and generates a one-time bearer token.
2. **Machine A** connects to Machine B using that token. Machine A's manager becomes an MCP client of Machine B's manager.
3. Machine A now sees all of Machine B's app sessions as if they were local. Any tool call targeting one of those sessions is transparently forwarded to Machine B.

The MCP client (e.g. Copilot) only ever talks to Machine A. It doesn't need to know anything about the underlying topology.

```
  Your AI tool
       │
       ▼
 ┌──────────────┐
 │  Machine A   │  ← you point Copilot here
 │  (root)      │
 └──┬───────┬───┘
    │       │
    ▼       ▼
 Machine B  Machine C
  App1       App3
  App2       App4
```

Chains can be deeper: Machine B can itself be connected to Machine D, and Machine A will see Machine D's apps too (with a `hops` value of 2 in the session metadata).

---

## Enabling External Access

By default, a Background Manager only accepts connections from the same machine. To allow other managers to connect, you need to explicitly enable external access.

### Via the dashboard

Open the Background Manager window and flip the **Allow external connections** toggle. The manager will:

- Start listening on your local network IP (port 5201 by default)
- Generate a fresh bearer token
- Show you a **Copy connection link** button

The connection link is a `zrover://` URI that encodes the URL and token. Clicking it on another machine (with Rover installed) will automatically connect that machine's manager to this one.

The token is regenerated every time you toggle external access on. Anyone who had a previous link will need a new one.

### Via protocol activation

You can also trigger this from the command line or a script by opening a `zrover://` URI:

| URI | Effect |
|-----|--------|
| `zrover://enable-external` | Enable external access on port 5201 |
| `zrover://enable-external?port=5300` | Enable on a custom port |
| `zrover://disable-external` | Disable external access |
| `zrover://status` | Bring the dashboard to the foreground |

On Windows, you can run these from a browser address bar, a Run dialog, or with `start zrover://enable-external`.

---

## Connecting to a Remote Manager

On the machine you want to act as the root (Machine A), open the dashboard and use the **Remote Managers** section.

**Using a connection link:** Open the `zrover://connect?url=...&token=...` link that was generated and copied on Machine B. This can be clicked in a browser or passed as a command-line argument to the Background Manager executable. The connection is established automatically.

Once connected, Machine B's apps appear in the sessions list with a `(via Machine-B)` label. They are immediately available to any MCP client connected to Machine A.

To disconnect, click **Disconnect** next to the manager in the dashboard.

---

## How Sessions Are Identified

When an app session is propagated from a remote machine, its session ID is prefixed with a short manager identifier to avoid collisions:

```
Local session:         abc123def456
From Machine B:        a1b2:abc123def456
From Machine C via B:  a1b2:c3d4:xyz789abc
```

The `hops` field in tool responses indicates how many manager-to-manager links a session has passed through:

- `hops: 0` — local session on the machine you're talking to
- `hops: 1` — one link away (directly connected machine)
- `hops: 2` — two links away, and so on

---

## Tool Calls Across Machines

From the perspective of an MCP client, there is no difference between a local and a remote session. You call `list_apps`, pick a session, call `set_active_app`, then invoke tools — the same as always.

Under the hood, when you invoke a tool on a remote session, the root manager:
1. Tells the remote manager to make that session active (`set_active_app`)
2. Forwards the tool call to the remote manager
3. Returns its result back to you

This adds one extra network round-trip per call. For most tools (screenshots, UI tree, gestures) this is imperceptible in practice.

**Note:** The remote manager only has one active session at a time, just like a local one. If you are rapidly alternating tool calls between two remote sessions on the same machine, each call will include a `set_active_app` step. For sustained interaction with a single remote app this is skipped automatically.

---

## Session Sync

When Machine B's session list changes (an app connects or disconnects), Machine A learns about it in real time via MCP's `tools/list_changed` notification — no polling. The session list on Machine A updates immediately.

If the network connection to a remote manager drops, a background health check detects this within ~10 seconds and marks the affected sessions as disconnected. They remain visible in the list so you can see what was lost.

---

## Security

- **Localhost-only by default.** A manager that has not enabled external access cannot be reached over the network, regardless of firewall settings.
- **Bearer token required.** Every request to an external listener must include the bearer token. There is no unauthenticated access.
- **Tokens are single-use per session.** Each time you toggle external access on, a new token is generated. Old tokens stop working immediately.
- **The token is in the connection link.** Treat the connection link like a password — anyone who has it can connect to your manager while external access is enabled.

---

## Things to Keep in Mind

- **One active session per manager.** Each Background Manager (local or remote) has a single active session slot. Setting a remote session active affects that remote manager's state for all clients connected to it.
- **Tool schema is uniform.** All Rover apps expose the same tools regardless of which machine they are on, so you don't need to think about schema differences when switching sessions.
- **Large payloads (screenshots, etc.) flow through the chain.** Screenshots are base64-encoded in the tool response and travel through each manager hop. On slow networks, large captures from deeply chained machines may take longer.
- **Disconnecting a manager removes its sessions.** When you disconnect a remote manager from the dashboard, all of its propagated sessions are removed from your local list immediately.
