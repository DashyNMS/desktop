# Ask about your network with Intelligence

Intelligence answers questions about your network in plain words: what's alerting, how a device is doing, where an address is plugged in or what changed overnight. It looks things up with DashyNMS's read-only network tools, so it can't change anything on your network.

An answer comes from one of three providers, each with your own account:

| Provider | Runs | Account |
| --- | --- | --- |
| ChatGPT | In DashyNMS | Your ChatGPT Plus or Pro plan |
| Codex | On this computer | Codex's own sign-in: a ChatGPT plan or an OpenAI API key |
| Claude Code | On this computer | Claude Code's own sign-in: a Claude plan or an Anthropic API key |

Each can also search the web for current public information, such as firmware releases, while **Allow web search** is on in **Settings › Intelligence**.

## Set up a provider

Open **Settings › Intelligence**. Each provider has a card that says whether it's ready and, if not, what to do.

### ChatGPT

Choose **Continue with ChatGPT**. Your browser opens to sign in, and DashyNMS never sees your password. You need ChatGPT Plus or Pro.

### Codex

1. Install Codex, for example with `npm install -g @openai/codex`. The Codex app's own copy also works on Windows.
2. Sign in once in a terminal with `codex login`.
3. In **Settings › Intelligence**, choose **Check again** on the Codex card.

### Claude Code

1. Install Claude Code, for example with `winget install Anthropic.ClaudeCode` on Windows or `curl -fsSL https://claude.ai/install.sh | bash` on macOS and Linux.
2. Sign in once in a terminal with `claude auth login`.
3. In **Settings › Intelligence**, choose **Check again** on the Claude Code card.

DashyNMS looks for Codex and Claude Code on your PATH and in their installers' usual folders. If yours is somewhere else, choose **Browse…** on its card and pick the executable.

A Claude subscription covers Claude Code. It doesn't give DashyNMS access to the Anthropic API, and DashyNMS never asks for an API key.

## Choose who answers

**Default provider** in **Settings › Intelligence** sets who answers new chats, along with each provider's default model and reasoning level.

To change it for one chat, use the model picker beside the question box. It has a tab for each provider that's ready, the provider's models, and, where the model has them, reasoning levels. More reasoning gives more careful answers, but they take longer and use more of your plan.

Each chat keeps the provider it started with. If you move a chat that already has messages to another provider, DashyNMS asks first. The chat's earlier questions and answers then go to the new provider so it can carry on. Lookup results, and anything the previous provider kept privately, stay behind.

## Usage and limits

Under the question box, DashyNMS shows the chat's usage as the provider reported it, for example `17% of 5-hour limit used · resets 18:30`. Codex and Claude Code report their plan's 5-hour and weekly windows; ChatGPT's route doesn't, so **Manage usage** on its card opens your usage page instead. DashyNMS never estimates usage or costs.

When a provider stops on a limit, the answer says so. Try again after it resets, or choose another provider.

## What Codex and Claude Code can do here

DashyNMS starts Codex and Claude Code in a locked-down session of its own:

- Their own tools are switched off: no shell commands, file edits, web browsing, plugins or sub-agents. Web search is on only while **Allow web search** is. Claude Code can search but not open pages itself, so nothing from your network can be passed to a website in an address.
- Your MCP servers, hooks, skills and settings files are left out of these chats. DashyNMS doesn't change your Codex or Claude Code configuration.
- They can reach only DashyNMS's read-only network tools, through a private connection on this computer. Your LibreNMS, Graylog and Unimus credentials stay in DashyNMS.
- They run in an empty folder of DashyNMS's own, so they never see your projects.

If an agent tries to use anything else, DashyNMS stops the answer and says so.

## What's sent and what's kept

Your questions, and what DashyNMS looks up to answer them, go to the company behind the provider: OpenAI for ChatGPT and Codex, Anthropic for Claude Code. That includes device names, addresses, alerts, ports and log lines. Passwords, tokens and SNMP communities are never sent. Config backups are summarised unless you choose otherwise in **Settings › Intelligence**.

DashyNMS keeps saved chats encrypted on this computer. Codex and Claude Code also keep their own record of each saved chat in their own folders, as they do for any conversation. **Single chat mode** keeps one temporary chat:

- With ChatGPT and Claude Code, nothing from it is kept on disk.
- With Codex, its thread lasts only while Codex is running.

See [Where DashyNMS keeps your data](../reference/your-data.md) and the [privacy notice](../../../PRIVACY.md).

## If something isn't working

| What you see | What to do |
| --- | --- |
| **Not installed** on a card | Install the agent, or choose its executable with **Browse…**, then choose **Check again**. |
| **Signed out** on a card | Run the sign-in command shown on the card in a terminal, then choose **Check again**. |
| A provider is missing from the model picker | The picker lists only providers that are ready. Check its card in **Settings › Intelligence**. |
| "couldn't reach DashyNMS's network tools" | Ask again. If it keeps happening, restart DashyNMS. |
| "tried to use one of its own tools" | DashyNMS stopped an answer that went outside its read-only tools. Ask again; if it happens every time, update the agent. |
