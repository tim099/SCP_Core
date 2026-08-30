## SCP_Core 共用規則

本專案以 `SCP_Core` 為 git submodule，跨專案的 agent 機制由它集中管理。
Codex 不支援 `@<path>` inline 載入，需要那批規則時請**顯式讀取**
[`{{SCP_CORE_PATH}}/AgentEntry/SCP_Core_Entry.md`]({{SCP_CORE_PATH}}/AgentEntry/SCP_Core_Entry.md)。
