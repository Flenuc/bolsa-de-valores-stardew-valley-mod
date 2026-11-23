using StardewModdingAPI;
using ZuzuMarket.Core.Events;
using ZuzuMarket.Core.Markets;
using ZuzuMarket.Core.Simulation;

namespace ZuzuMarket.Game.Content;

/// <summary>Titular de cada noticia económica (Terminal, comandos y avisos).</summary>
internal static class NewsText
{
    public static string Issuer(ITranslationHelper i18n, EconomySimulation sim, string issuer) =>
        issuer == CreditEngine.Sovereign
            ? i18n.Get("issuer.sovereign")
            : sim.Stocks.Find(issuer)?.Name
                ?? (sim.Companies.State.Companies.FirstOrDefault(c => c.Ticker == issuer) is { } own ? CompanyMail.TypeName(i18n, own.Type) + " " + i18n.Get($"company.form.{own.Form}") : issuer);

    public static string Headline(ITranslationHelper i18n, EconomySimulation sim, NewsItem n)
    {
        switch (n.Kind)
        {
            case NewsKind.Event:
                return i18n.Get($"event.{n.Key}.headline");
            case NewsKind.Inflation:
                return i18n.Get("news.inflation.short", new { rate = Formatting.Percent(i18n, n.Value) });
            case NewsKind.RateDecision:
                return i18n.Get($"news.rate.{n.Key}", new { rate = Formatting.Percent(i18n, n.Value) });
            case NewsKind.Default:
                return i18n.Get("news.default", new { issuer = Issuer(i18n, sim, n.Key) });
            case NewsKind.Restructuring:
                return i18n.Get("news.restructuring", new { issuer = Issuer(i18n, sim, n.Key) });
            case NewsKind.Devaluation:
                return i18n.Get("news.devaluation", new { size = Formatting.Percent(i18n, n.Value), rate = Formatting.Money(i18n, (decimal)sim.Fx.Official) });
            case NewsKind.RatingChange:
                string[] parts = n.Key.Split(':');
                return i18n.Get(n.Value > 0 ? "news.upgrade" : "news.downgrade", new
                {
                    issuer = Issuer(i18n, sim, parts[0]),
                    rating = parts.Length > 1 ? parts[1] : "",
                });
            default:
                return n.Key;
        }
    }
}
