---
name: shopping-list
description: Adds groceries and household items to the shared shopping list and continues a spoken conversation about more items. Use when someone asks to add items to the shopping list or says what else to put on it.
---

## Add items conversationally

- Add every item the user explicitly names in the current turn. A batch such as "melk, skinke og ost" should be added as one requested batch; do not ask separately about each item.
- Use the available Home Assistant shopping-list tools. Do not claim an item was added unless the tool confirms it.
- After a successful batch, confirm what was added and ask whether the user wants anything else added. Mark the response as needing a follow-up so Voice PE opens its microphone.
- If the user names another batch in response, add that batch, confirm it, and ask again.
- Continue until the user explicitly says no, is finished, or otherwise declines. Do not add a decline such as "nei" or "det var alt" as an item. Then say the list is finished and end the follow-up.
- If the user's reply is ambiguous or unrelated to adding items, ask what they want to do; do not turn arbitrary conversation into a shopping-list entry.
- If any tool reports failure or only some items are confirmed, be clear about what succeeded. Some actions may already have completed; ask the user to check the list before retrying instead of claiming the whole batch failed.
