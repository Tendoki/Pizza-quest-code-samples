using System.Threading;
using Cysharp.Threading.Tasks;

namespace DialogueSystem
{
    public interface IDialoguePresentation
    {
        UniTask PlayTypingAsync(CancellationToken ct);
        void ShowLineInstant();
    }
}
