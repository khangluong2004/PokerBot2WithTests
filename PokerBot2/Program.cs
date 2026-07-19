using PokerBot2;

// '♠♣♦♥'
int[] hand = [
    Game.StringToCard("2♠"),
    Game.StringToCard("2♣"),
    Game.StringToCard("3♣"),
    Game.StringToCard("2♦"),
    Game.StringToCard("A♠"),
    Game.StringToCard("2♥"),
    Game.StringToCard("K♥")
];

Console.WriteLine(string.Join(", ", hand));
Console.WriteLine(Game.EvalHand(hand, 0));

int[] fullhouse = [
    Game.StringToCard("3♠"),
    Game.StringToCard("2♣"),
    Game.StringToCard("3♣"),
    Game.StringToCard("2♦"),
    Game.StringToCard("A♠"),
    Game.StringToCard("2♥"),
    Game.StringToCard("J♥")
];

Console.WriteLine(string.Join(", ", fullhouse));
(int highestCard, WinHandType winHand) = Game.EvalHand(fullhouse, 0);
Console.WriteLine($"{highestCard} {winHand}");
Console.WriteLine($"Expected highest 'card' {(0 << 6) + 1}");

int[] fullhouse2 = [
    Game.StringToCard("3♠"),
    Game.StringToCard("2♣"),
    Game.StringToCard("3♣"),
    Game.StringToCard("2♦"),
    Game.StringToCard("3♠"),
    Game.StringToCard("2♥"),
    Game.StringToCard("J♥")
];
(highestCard, winHand) = Game.EvalHand(fullhouse2, 0);
Console.WriteLine($"{highestCard} {winHand}");
Console.WriteLine($"Expected highest 'card' {(1 << 6) + 0}");