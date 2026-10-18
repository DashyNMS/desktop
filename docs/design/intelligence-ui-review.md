# Intelligence usability review

Reviewed 18 October 2026. Scope: the Intelligence tab, composer, chat list and Context pane. Findings use the current XAML, command handlers and an isolated WPF rendering; this is a design and code review, not a completed keyboard, screen-reader or accessibility certification.

## Recommended composer

User selected **Integrated controls**, with visible **Model** and **Reply style** labels and no visible "Message Dashy" label. The local implementation now keeps the question box anchored below the conversation, aligned to the message column, with the two pickers together inside the composer and Ask/Stop at the trailing edge. Both pickers disable while answering. The entry has an accessible name and a quiet keyboard-shortcut hint; the optional companion stays on the composer's top edge.

## Findings and proposed work

| Priority | Observation | Proposed change |
| --- | --- | --- |
| High | The Route sparkle in the Intelligence header currently invokes Show/hide saved chats. Its appearance identifies Intelligence but does not describe this action. | Use Route sparkle as the section identity; use a separate, recognisable pane toggle for saved chats. Keep accessible names and tooltips on icon buttons. |
| Addressed locally | The model and style pickers previously lacked visible labels, and only reply style disabled while answering. | Integrated composer now labels both selectors and disables them together. The entry and model have explicit accessible names, with no additional visible entry label. A shortcut hint is shown. Full screen-reader verification remains outstanding. |
| Medium | Rename and pin are available through the saved chat's context menu; the visible action is Delete. A user may not discover the non-destructive actions. | Verify keyboard context-menu access and accessible names; consider a small chat actions menu if testing shows poor discovery. Preserve the existing delete confirmation. |
| Medium | Fixed/minimum selector widths share space with Ask/Stop and two side panes. WrapPanel helps but narrow layouts need direct verification. | Test with both panes open, minimum window size, long model names and 125–200% scaling. Wrap controls before reducing the question entry or obscuring Ask/Stop. |

## Rest of the tab: detailed findings

### High: reading position is not respected

`IntelligenceView.xaml.cs`, `OnMessagesChanged`, subscribes every new message's **every** property change to `Transcript.ScrollToEnd()`. This includes streaming text, expanding lookup details and the Copied feedback timer. Reading an earlier answer can therefore jump to the latest content even when the user is not following the stream.

Proposed fix: follow only while already near the bottom; stop following after the user scrolls up. Offer a clear Jump to latest action when new content arrives. UI-only property changes should not scroll the transcript. Detach message subscriptions when messages leave the view.

### High: Context disappears without a narrow-window access path

`UpdateSidebar` requires a root width of 1000 DIPs, plus 220 when Chats is open, to show Context. The Show Context button uses the same `room` condition. Below that threshold, both the pane and its entry point disappear. The main window defaults to 1180 DIPs before subtracting app navigation, so this affects normal window sizes, not just exceptional small windows.

Proposed fix: keep the Context control available and open an overlay/drawer when an inline pane will not fit. Keep Chats and Context toggles symmetrical and clearly named.

### High: alert acknowledgement obscures severity

`ContextAlertViewModel` sets `Severity` to `other` whenever an alert is acknowledged; `StatusDotStyle` renders that as neutral grey. A critical acknowledged alert consequently loses its critical severity indicator. The row does not separately expose acknowledgement or a consistent severity label.

Proposed fix: preserve the actual severity colour and readable severity label. Show Acknowledged as a separate state/badge. Keep device Up/Down separate from alert Critical/Warning. These are independent facts.

### High: Spotlight can change while the answer uses the previous scope

`SendAsync` captures `Context.SpotlightForAgent` once. Spotlight add/remove/clear commands remain enabled independently of the conversation's busy state. During a reply the header can show a new scope while the running question still uses the earlier captured scope.

Proposed fix: disable Spotlight editing during an answer, or explicitly identify scope changes as applying to the next question. Changing scope should never suggest that a completed answer was regenerated.

### Medium: live sidebar data and reply observations need distinct labels

The Context pane refreshes device/alert data from the app's cache and successful alert polls. Inline answer references deliberately retain their original observations. Neither the sidebar's Devices heading nor its Alerts heading explains that distinction. Alerts are filtered to mentioned devices, capped at eight, and `AlertsHeader` shows only the displayed count; it does not indicate the full matching count or that the list is a subset.

Proposed fix: label sections Mentioned devices and Open alerts on mentioned devices. Show the last successful refresh and distinguish live/cached sidebar data from the answer's observation time. Where more than eight match, show "Showing 8 of N". Do not present a capped list as the whole-network total.

### Medium: failed and stopped answers need clearer outcomes and recovery

The question entry is cleared before the request runs. Failure can remove the assistant message and leave a general top banner without an inline retry/edit control, requiring the user to copy their own earlier question. `CompletionGlyph` considers failed lookups and restored interruption, but not cancellation during text generation: a stopped answer whose lookups succeeded can retain a success tick.

Proposed fix: distinguish Completed, Stopped, Interrupted and Failed. Add an accessible Retry/Edit question action next to the failed turn, preserving the submitted question and partial answer. A lookup success tick should not imply a complete or factually validated answer.

### Medium: chat identity and actions become hard to discover

The main header always says Intelligence. When Chats is collapsed, the active chat title is not visible there. Rename appears in a separate form at the bottom of the chat list, away from its target, while Delete is the immediately visible row action. The chat-list tooltip always tells users to finish/stop an answer even when the app is idle. Preview text expands inline markers but still includes Markdown emphasis such as literal `**`.

Proposed fix: show the active chat title near the section title, keep rename beside its target, preserve right-click rename/pin, and make the busy tooltip conditional. Consider a small actions menu only if discovery testing warrants it; the user previously requested the visible bin and right-click actions. Convert previews to plain readable text without changing copied Markdown.

### Medium: graph enlargement lacks a keyboard action

Graphs use a Border with `MouseLeftButtonUp`, a Hand cursor and a tooltip. That does not provide a standard focusable button or keyboard invocation.

Proposed fix: use a semantic button with an accessible name such as Open graph full size, keeping a visible keyboard focus cue. Verify the full-size window can be closed and focus returns to the originating graph.

### Checks still required, rather than confirmed defects

- Actual screen-reader output, focus visibility in custom chat/Context/lookup templates, and sensible pane navigation. Consider F6 navigation between Chats, transcript/composer and Context.
- Error and busy announcements that do not read every streamed token.
- High-contrast behaviour of the hardcoded navy response palette and coloured markers. The palette intentionally stays dark in both app themes; that does not by itself establish high-contrast support.
- Long Spotlight names, restored large chats, minimum-height windows, long model names, mixed DPI and large text.

## Keep

Keep the aligned pane headers, distinct message colours, bounded reading width, expandable execution receipts, readable Markdown copy with Copied feedback, explicit status text, stable observed references across pagination, chat pinning/activity ordering, conditional Notes footer and optional companion. These already give the tab a coherent structure; the priority is fixing the confusing behaviour around them.

## Verification to schedule before implementation sign-off

- Keyboard-only route: new chat → question → model → reply style → Ask → Stop → history → rename/pin/delete.
- UI Automation names, selected values, expanded states and busy announcements, checked with a screen reader or accessibility inspector.
- Light/dark themes, high contrast, visible focus, neutral status indicators and text scaling.
- Empty chat, restored draft, streaming answer, cancellation, lookup failure, expired sign-in and disconnected network.
- Companion enabled/disabled, long responses, scroll position and reaching the latest answer.

## Guidance

The placement recommendations above are product-specific design judgements. Microsoft describes combo boxes as suitable for secondary choices, supporting keeping them below the primary writing area: https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/combo-box

Microsoft's tooltip guidance supports labelling icon buttons and cautions against designs that require repeatedly consulting a tooltip to understand a control: https://learn.microsoft.com/en-us/windows/win32/uxguide/ctrl-tooltips-and-infotips

Microsoft's keyboard accessibility guidance recommends predictable keyboard access, meaningful pane names and visible focus: https://learn.microsoft.com/windows/apps/design/accessibility/keyboard-accessibility

Route sparkle and the selected integrated composer are implemented locally. Findings about the rest of the tab are proposed follow-up work, not implemented fixes. No new PR or commit has been created for this review.
