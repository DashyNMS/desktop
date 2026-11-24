# Ask about your network with Intelligence

Intelligence answers questions about your network in plain words: what's alerting, how a device is doing, where an address is plugged in or what changed overnight. It looks things up with DashyNMS's read-only network tools, so it can't change anything on your network.

An answer comes from one of these providers, each with your own account:

| Provider | Runs | Account |
| --- | --- | --- |
| ChatGPT | In DashyNMS, or through Codex on this computer | Your ChatGPT plan, or Codex's own sign-in |
| Claude Code | On this computer | Claude Code's own sign-in: a Claude plan or an Anthropic API key |
| GitHub Copilot | On this computer, through the Copilot CLI | The Copilot CLI's own sign-in, on any Copilot plan, Free included |
| Gemini | On this computer, through Antigravity CLI | Antigravity's own sign-in with your Google account: free, AI Pro or Ultra |

ChatGPT, Claude Code and Gemini can also search the web for current public information, such as firmware releases, while **Allow web search** is on in **Settings › Intelligence**. GitHub Copilot answers without web search.

## Set up a provider

Open **Settings › Providers**. The list shows the providers you've added, each with whether it's ready. Choose one to see its connection, account, usage, models and executable. **Add provider** under the list shows every provider DashyNMS supports: choose one to add it, or, if it isn't installed yet, to open its install page.

To remove a provider, choose the bin beside it. Its chats are kept for when you add it again. Removing ChatGPT also signs DashyNMS out of your ChatGPT account. The agents on this computer keep their own sign-in.

### ChatGPT

ChatGPT connects one of two ways. Choose one under **Connect with**:

- **Sign in**: your browser opens to sign in, and DashyNMS never sees your password. You need ChatGPT Plus or Pro.
- **Codex**: uses Codex on this computer with its own sign-in. Install it, for example with `npm install -g @openai/codex` (the Codex app's own copy also works on Windows), sign in once with `codex login`, then choose **Check again**.

Chats made the other way stay saved, and come back when you switch back.

### Claude Code

1. Install Claude Code, for example with `winget install Anthropic.ClaudeCode` on Windows or `curl -fsSL https://claude.ai/install.sh | bash` on macOS and Linux.
2. Sign in once in a terminal with `claude auth login`.
3. Choose **Check again**.

A Claude subscription covers Claude Code. It doesn't give DashyNMS access to the Anthropic API, and DashyNMS never asks for an API key.

### GitHub Copilot

1. Install the GitHub Copilot CLI, for example with `winget install GitHub.Copilot` on Windows or `npm install -g @github/copilot`. VS Code's `copilot` command only offers to install the CLI, so DashyNMS looks for the CLI itself.
2. Sign in once in a terminal with `copilot login`.
3. Choose **Check again**.

The account needs a Copilot plan; Copilot Free works, with its Auto model. DashyNMS's network tools reach Copilot as an MCP server, so **MCP servers in Copilot** must be on at [github.com/settings/copilot/features](https://github.com/settings/copilot/features). If an organisation manages your Copilot, its administrator decides both. When either is off, Copilot shows as **Unavailable** and says which.

### Gemini

1. Install Antigravity CLI, Google's terminal agent: `winget install Google.AntigravityCLI` on Windows, or the installer on [antigravity.google](https://antigravity.google) on macOS and Linux.
2. Sign in once by running `agy` in a terminal.
3. Choose **Check again**.

Gemini's models, including others Google offers through Antigravity, come from your account and are listed as Antigravity names them.

DashyNMS looks for each agent on your PATH and in its installer's usual folders. If yours is somewhere else, choose **Browse…** under **Executable** and pick it.

## Choose who answers

**Default provider** in **Settings › Providers** sets who answers new chats. Each provider's **Default setup** sets its model and reasoning level for new chats, and **Available models** chooses which of its models the picker shows.

To change it for one chat, use the model picker beside the question box. It has a tab for each provider that's ready, the provider's models, and, where the model has them, reasoning levels. More reasoning gives more careful answers, but they take longer and use more of your plan.

Each chat keeps the provider it started with. If you move a chat that already has messages to another provider, DashyNMS asks first. The chat's earlier questions and answers then go to the new provider so it can carry on. Lookup results, and anything the previous provider kept privately, stay behind. The conversation marks the move with an **Agent changed** line, from the old model to the new one.

## Give Dashy a personality

In **Settings › Intelligence › Personality**, describe how you'd like Dashy to sound, for example "Be blunt and brief. Lead with what's broken." Tick **Use a custom personality** and apply. While it's on, **Custom personality** shows under the question box.

A personality changes tone and wording only. Dashy still checks its facts, uses only the read-only tools, says what it couldn't confirm and keeps to the chat's detail level, whatever the description asks. It can be up to 600 characters. It's sent with each question to the provider answering it, and saved with your settings on this computer, not in chats. Turning it off keeps the text for later. Copilot and Gemini take no separate instructions, so for them DashyNMS sends the personality with a chat's first question, and a change applies from the next new chat.

## Usage and limits

Under the question box, DashyNMS shows the chat's usage as the provider reported it, for example `17% of 5-hour limit used · resets 18:30`. Codex and Claude Code report their plan's 5-hour and weekly windows, and GitHub Copilot its monthly allowance. ChatGPT's sign-in route and Gemini don't report limits, so none is shown for them. DashyNMS never estimates usage or costs.

When a provider stops on a limit, the answer says so. Try again after it resets, or choose another provider.

Separately from your plan, DashyNMS limits what Intelligence asks of your own servers: at most 8 requests at a time to each of LibreNMS, Unimus and Graylog. A question that needs more lookups waits its turn, so it can take a little longer on a busy network.

## What the agents can do here

DashyNMS starts Codex, Claude Code, Copilot and Gemini in a locked-down session of its own:

- Their own tools are kept out: no shell commands, file edits, web browsing, plugins or sub-agents. Codex, Claude Code and Copilot have them switched off. Antigravity's can't be switched off, so DashyNMS refuses them with rules for that session only and stops the answer if one gets through.
- Web search is on only while **Allow web search** is. Claude Code can search but not open pages itself, so nothing from your network can be passed to a website in an address. Antigravity's search can't be switched off, so with web search off DashyNMS stops a Gemini answer as soon as it starts searching.
- Your MCP servers, hooks and skills are left out of these chats, and DashyNMS doesn't change your agents' configuration. Codex still reads the rest of its own configuration, such as a custom model provider, and Gemini reads your global Antigravity rules files, if you have any.
- They can reach only DashyNMS's read-only network tools, through a private connection on this computer. Your LibreNMS, Graylog and Unimus credentials stay in DashyNMS.
- They run in an empty folder of DashyNMS's own, so they never see your projects.

If an agent tries to use anything else, DashyNMS stops the answer and says so.

## What's sent and what's kept

Your questions, and what DashyNMS looks up to answer them, go to the company behind the provider: OpenAI for ChatGPT and Codex (or, for Codex, whichever model provider its own configuration names), Anthropic for Claude Code, GitHub for Copilot (which passes them on to the chosen model's maker) and Google for Gemini. That includes device names, addresses, alerts, ports and log lines, and your personality description if you've set one. Passwords, tokens and SNMP communities are never sent. Config backups are summarised unless you choose otherwise in **Settings › Intelligence**.

DashyNMS keeps saved chats encrypted on this computer. The agents also keep their own record of each saved chat in their own folders, as they do for any conversation, and deleting a chat in DashyNMS leaves that record where it is. **Single chat mode** keeps one temporary chat:

- With ChatGPT and Claude Code, nothing from it is kept on disk.
- With Copilot and Gemini, DashyNMS deletes their record of it once each answer is in, if it can.
- With Codex, its thread lasts only while Codex is running.

See [Where DashyNMS keeps your data](../reference/your-data.md) and the [privacy notice](../../../PRIVACY.md).

## If something isn't working

| What you see | What to do |
| --- | --- |
| **Not installed** | Install the agent, or choose its executable with **Browse…**, then choose **Check again**. |
| **Signed out** | Run the sign-in command shown for the provider in a terminal, then choose **Check again**. |
| **Unavailable** | Read the reason shown. For Copilot it's usually the account's plan, or MCP servers turned off. |
| A provider is missing from the model picker | The picker lists only providers that are ready. Check it in **Settings › Providers**. |
| "couldn't reach DashyNMS's network tools" | Ask again. If it keeps happening, restart DashyNMS. |
| "tried to use one of its own tools" | DashyNMS stopped an answer that went outside its read-only tools. Ask again; if it happens every time, update the agent. |
