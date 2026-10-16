namespace Sword.Engine.Ui;

/// <summary>
/// Правка однострочного текста: каретка, выделение, слова — операции над строкой без отрисовки. Каретка не встаёт между
/// половинками пары UTF-16: ←/→, Backspace и Delete проходят пару целиком.
/// </summary>
public sealed class UiTextEdit
{
    /// <summary>Позиция каретки: перед символом с этим индексом.</summary>
    public int Caret;
    /// <summary>Второй конец выделения; равен <see cref="Caret"/> — выделения нет.</summary>
    public int Anchor;
    /// <summary>Сдвиг текста влево, чтобы каретка оставалась в поле.</summary>
    public float ScrollX;
    /// <summary>Время последней правки или движения — каретка после них видна сразу, без мигания.</summary>
    public double LastChange;
    /// <summary>Чем закончилось редактирование в последний раз: Enter, Escape или потеря фокуса.</summary>
    public UiEditExit LastExit;
    /// <summary>Кнопка мыши зажата после двойного щелчка: протяжка не трогает выделенное слово.</summary>
    public bool WordPress;

    public bool HasSelection => Caret != Anchor;
    public int SelectionStart => Math.Min(Caret, Anchor);
    public int SelectionEnd => Math.Max(Caret, Anchor);

    /// <summary>Каретку и выделение — в пределы строки (её могли сменить снаружи) и не внутрь пары UTF-16.</summary>
    public void Clamp(string text)
    {
        Caret = Snap(text, Caret);
        Anchor = Snap(text, Anchor);
    }

    public void SelectAll(string text)
    {
        Anchor = 0;
        Caret = text.Length;
    }

    public string Selected(string text) => text[SelectionStart..SelectionEnd];

    public string DeleteSelection(string text)
    {
        if (!HasSelection) return text;
        int start = SelectionStart;
        text = text.Remove(start, SelectionEnd - start);
        Caret = Anchor = start;
        return text;
    }

    /// <summary>Вставить вместо выделения; переводы строк заменяются пробелами — поле однострочное.</summary>
    public string Insert(string text, string insert)
    {
        insert = insert.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
        text = DeleteSelection(text);
        text = text.Insert(Caret, insert);
        Caret = Anchor = Caret + insert.Length;
        return text;
    }

    public string Backspace(string text, bool word)
    {
        if (HasSelection) return DeleteSelection(text);
        Caret = Anchor = Snap(text, Caret);
        if (Caret == 0) return text;
        int from = word ? WordLeft(text, Caret) : PrevChar(text, Caret);
        text = text.Remove(from, Caret - from);
        Caret = Anchor = from;
        return text;
    }

    public string Delete(string text, bool word)
    {
        if (HasSelection) return DeleteSelection(text);
        Caret = Anchor = Snap(text, Caret);
        if (Caret >= text.Length) return text;
        int to = word ? WordRight(text, Caret) : NextChar(text, Caret);
        return text.Remove(Caret, to - Caret);
    }

    public void MoveTo(string text, int index, bool extend)
    {
        Caret = Snap(text, index);
        if (!extend) Anchor = Caret;
    }

    public void Left(string text, bool word, bool extend)
    {
        if (!extend && HasSelection && !word)
        {
            Caret = Anchor = SelectionStart;
            return;
        }
        MoveTo(text, word ? WordLeft(text, Caret) : PrevChar(text, Caret), extend);
    }

    public void Right(string text, bool word, bool extend)
    {
        if (!extend && HasSelection && !word)
        {
            Caret = Anchor = SelectionEnd;
            return;
        }
        MoveTo(text, word ? WordRight(text, Caret) : NextChar(text, Caret), extend);
    }

    /// <summary>Выделить слово вокруг позиции (двойной щелчок).</summary>
    public void SelectWord(string text, int index)
    {
        index = Math.Clamp(index, 0, text.Length);
        int start = index, end = index;
        while (start > 0 && !IsSeparator(text[start - 1])) start--;
        while (end < text.Length && !IsSeparator(text[end])) end++;
        Anchor = start;
        Caret = end;
    }

    private static bool IsSeparator(char c) => char.IsWhiteSpace(c) || char.IsPunctuation(c) && c is not ('_' or '-');

    /// <summary>Начало слова слева от <paramref name="index"/>: пропустить разделители, затем слово.</summary>
    public static int WordLeft(string text, int index)
    {
        int i = Math.Clamp(index, 0, text.Length);
        while (i > 0 && IsSeparator(text[i - 1])) i--;
        while (i > 0 && !IsSeparator(text[i - 1])) i--;
        return i;
    }

    /// <summary>Конец слова справа от <paramref name="index"/>: пропустить разделители, затем слово.</summary>
    public static int WordRight(string text, int index)
    {
        int i = Math.Clamp(index, 0, text.Length);
        while (i < text.Length && IsSeparator(text[i])) i++;
        while (i < text.Length && !IsSeparator(text[i])) i++;
        return i;
    }

    /// <summary>Позиция в строке, не внутри пары UTF-16: между половинками — перед парой.</summary>
    private static int Snap(string text, int index)
    {
        int i = Math.Clamp(index, 0, text.Length);
        if (i > 0 && i < text.Length && char.IsLowSurrogate(text[i]) && char.IsHighSurrogate(text[i - 1])) i--;
        return i;
    }

    /// <summary>Позиция символом левее: пара UTF-16 — целиком.</summary>
    private static int PrevChar(string text, int index)
    {
        int i = Snap(text, index);
        if (i >= 2 && char.IsLowSurrogate(text[i - 1]) && char.IsHighSurrogate(text[i - 2])) return i - 2;
        return Math.Max(i - 1, 0);
    }

    /// <summary>Позиция символом правее: пара UTF-16 — целиком.</summary>
    private static int NextChar(string text, int index)
    {
        int i = Snap(text, index);
        if (i + 1 < text.Length && char.IsHighSurrogate(text[i]) && char.IsLowSurrogate(text[i + 1])) return i + 2;
        return Math.Min(i + 1, text.Length);
    }
}

public enum UiEditExit
{
    None,
    /// <summary>Enter: принять.</summary>
    Commit,
    /// <summary>Escape: отменить.</summary>
    Cancel,
    /// <summary>Щелчок мимо поля: принять.</summary>
    Blur,
}
