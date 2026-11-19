namespace Hearthpulse.Core.Cards;

/// <summary>
/// The card fields the simulator reads. In Hearthpulse this is the full card record from HearthstoneJSON; here the
/// host fills only what <see cref="Battlegrounds.Effects.CombatRelevance"/> needs for cards its table does not know.
/// </summary>
public sealed class Card
{
    public required string Id { get; init; }

    public int DbfId { get; init; }

    /// <summary>Card text in the locale the host loaded (the text heuristic runs only on English text).</summary>
    public string Text { get; init; } = string.Empty;

    public IReadOnlyList<string> Mechanics { get; init; } = Array.Empty<string>();

    /// <summary>For a golden Battlegrounds card, the dbf id of its normal version; 0 otherwise.</summary>
    public int BattlegroundsNormalDbfId { get; init; }
}

/// <summary>Card lookup by card id and dbf id.</summary>
public sealed class CardDb
{
    private readonly Dictionary<string, Card> _byId = new(StringComparer.Ordinal);
    private readonly Dictionary<int, Card> _byDbf = [];

    public CardDb(IEnumerable<Card> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        foreach (var card in cards)
        {
            _byId[card.Id] = card;
            if (card.DbfId > 0)
            {
                _byDbf[card.DbfId] = card;
            }
        }
    }

    public Card? Get(string? cardId) => cardId is { Length: > 0 } && _byId.TryGetValue(cardId, out var card) ? card : null;

    public Card? Get(int dbfId) => _byDbf.GetValueOrDefault(dbfId);
}
