// 區塊職責：netstandard2.1 缺的編譯器支援型別。
// 物理意義：`init` 存取子（C# 9）需要 System.Runtime.CompilerServices.IsExternalInit，
//           而 netstandard2.1 的 BCL 沒有它 —— 少這一顆，用了 init 的檔案會編不過。
//           宣告成 internal ⇒ 每個 assembly 各有一份不會衝突（Unity 那邊也是同一個道理）。
// 數值影響：純編譯期標記，執行期不存在任何行為。
#nullable enable
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
