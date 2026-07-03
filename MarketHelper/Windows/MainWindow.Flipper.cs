using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using MarketHelper;
using static MarketHelper.UiScale;

namespace MarketHelper.Windows;

public partial class MainWindow
{
    private string _flipQuery = string.Empty;
    private Flipper? _flipper;
    private Flipper Flip => _flipper ??= new Flipper();

    // toggles
    private bool _showBestServer = true;

    private void DrawFlipperTab()
    {
        WrapText("Search an item to compare prices across Japan, America, Europe and Materia (via Universalis). Cross-region: find where it's cheapest to buy and dearest to sell.");
        Dummy(4f);

        // --- Search box ---
        ImGui.SetNextItemWidth(SW(280));
        ImGui.InputTextWithHint("##flipsearch", "Type an item name...", ref _flipQuery, 100);
        ImGui.SameLine(0, SW(6));
        var hq = Flip.HqOnly;
        if (ImGui.Checkbox("HQ only", ref hq)) Flip.HqOnly = hq;

        // Live suggestions
        if (_flipQuery.Trim().Length >= 2)
        {
            var hits = ItemSearch.Find(_flipQuery);
            if (hits.Count > 0 && !(hits.Count == 1 && hits[0].Name == Flip.ItemName))
            {
                if (ImGui.BeginChild("##suggest", new Vector2(SW(280), SW(140)), true))
                {
                    foreach (var h in hits)
                    {
                        if (ImGui.Selectable($"{h.Name}##{h.Id}"))
                        {
                            _flipQuery = h.Name;
                            Flip.Search(h.Id, h.Name);
                        }
                    }
                }
                ImGui.EndChild();
            }
        }

        Dummy(4f);

        if (Flip.Loading)
        {
            ImGui.TextColored(Gold, $"Searching markets for {Flip.ItemName}...");
            return;
        }
        if (!string.IsNullOrEmpty(Flip.Error))
        {
            ImGui.TextColored(Red, $"Error: {Flip.Error}");
            return;
        }
        if (!Flip.HasResults)
        {
            if (!string.IsNullOrEmpty(Flip.ItemName))
            {
                ImGui.TextColored(Grey, $"No market data found for {Flip.ItemName}.");
                // Surface any per-region errors to aid diagnosis.
                foreach (var region in Flipper.Regions)
                {
                    var r = Flip.Results.GetValueOrDefault(region);
                    if (r != null && !string.IsNullOrEmpty(r.Error))
                        ImGui.TextColored(Red, $"{Flipper.RegionLabel[region]}: {r.Error}");
                }
            }
            return;
        }

        // --- Flip summary ---
        ImGui.Text($"Item: ");
        ImGui.SameLine(0, SW(4));
        ImGui.TextColored(Gold, Flip.ItemName);

        var flip = Flip.BestFlip();
        if (flip.Valid)
        {
            Dummy(2f);
            ImGui.TextColored(Green, "Best cross-region flip:");
            ImGui.Indent(SW(10));
            ImGui.TextColored(Grey, "Buy:");
            ImGui.SameLine();
            ImGui.Text($"{Flipper.RegionLabel[flip.BuyRegion]} ({flip.BuyWorld}) @ {flip.BuyPrice:N0}g");
            ImGui.TextColored(Grey, "Sell:");
            ImGui.SameLine();
            ImGui.Text($"{Flipper.RegionLabel[flip.SellRegion]} ({flip.SellWorld}) @ {flip.SellPrice:N0}g");
            ImGui.TextColored(flip.Profit > 0 ? Green : Red, $"Raw spread: {flip.Profit:N0}g per unit");

            // After-tax net: pay buyPrice*(1+buyerTax) when buying cross-city; receive
            // sellPrice*(1-sellerTax) when selling.
            var sellerRate = Cfg.ApplySellerTax ? Cfg.SellerTaxPercent / 100f : 0f;
            var buyerRate = Cfg.ApplyBuyerTax ? Cfg.BuyerTaxPercent / 100f : 0f;
            var netReceive = flip.SellPrice * (1f - sellerRate);
            var netPay = flip.BuyPrice * (1f + buyerRate);
            var netProfit = (long)Math.Round(netReceive - netPay);
            if (Cfg.ApplySellerTax || Cfg.ApplyBuyerTax)
            {
                ImGui.TextColored(netProfit > 0 ? Green : Red,
                    $"After tax: {netProfit:N0}g per unit");
                ImGui.TextColored(Grey,
                    $"(buy {netPay:N0}g incl. {Cfg.BuyerTaxPercent:0.#}% buyer tax; " +
                    $"receive {netReceive:N0}g after {Cfg.SellerTaxPercent:0.#}% seller tax)");
            }
            ImGui.Unindent(SW(10));
        }

        // --- Tax controls ---
        Dummy(2f);
        var sellerTax = Cfg.ApplySellerTax;
        if (ImGui.Checkbox("Seller tax", ref sellerTax)) { Cfg.ApplySellerTax = sellerTax; Cfg.Save(); }
        ImGui.SameLine(0, SW(6));
        HelpMarker("Reduces what you receive when selling. Standard 5% in Ul'dah / Limsa / Gridania; reduced or 0% in expansion hubs (Ishgard, Kugane, Crystarium) depending on retainer count.");
        if (Cfg.ApplySellerTax)
        {
            ImGui.SameLine(0, SW(10));
            ImGui.SetNextItemWidth(SW(90));
            var st = Cfg.SellerTaxPercent;
            if (ImGui.SliderFloat("##sellertax", ref st, 0f, 5f, "%.1f%%")) { Cfg.SellerTaxPercent = st; Cfg.Save(); }
        }

        var buyerTax = Cfg.ApplyBuyerTax;
        if (ImGui.Checkbox("Buyer tax", ref buyerTax)) { Cfg.ApplyBuyerTax = buyerTax; Cfg.Save(); }
        ImGui.SameLine(0, SW(6));
        HelpMarker("Buyers pay 5% when buying from a Market Board in a different city-state than their retainer's home city. No tax if buying in your retainer's home city.");
        if (Cfg.ApplyBuyerTax)
        {
            ImGui.SameLine(0, SW(10));
            ImGui.SetNextItemWidth(SW(90));
            var bt = Cfg.BuyerTaxPercent;
            if (ImGui.SliderFloat("##buyertax", ref bt, 0f, 5f, "%.1f%%")) { Cfg.BuyerTaxPercent = bt; Cfg.Save(); }
        }

        Dummy(4f);
        ImGui.Separator();

        // --- Per-region columns ---
        if (ImGui.BeginTable("##flipregions", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg))
        {
            foreach (var region in Flipper.Regions)
                ImGui.TableSetupColumn(Flipper.RegionLabel[region]);
            ImGui.TableHeadersRow();

            // Row 0: cheapest summary per region
            ImGui.TableNextRow();
            foreach (var region in Flipper.Regions)
            {
                ImGui.TableNextColumn();
                var r = Flip.Results.GetValueOrDefault(region);
                if (r == null || !r.HasData)
                {
                    ImGui.TextColored(Grey, r?.Error ?? "no data");
                    continue;
                }
                ImGui.TextColored(Gold, $"{r.CheapestPrice:N0}g");
                if (_showBestServer)
                    ImGui.TextColored(Grey, r.CheapestWorld);
            }

            // Top-10 listings per region
            for (var i = 0; i < 10; i++)
            {
                ImGui.TableNextRow();
                foreach (var region in Flipper.Regions)
                {
                    ImGui.TableNextColumn();
                    var r = Flip.Results.GetValueOrDefault(region);
                    if (r == null || i >= r.Listings.Count) continue;
                    var l = r.Listings[i];
                    var hqMark = l.Hq ? " [HQ]" : string.Empty;
                    ImGui.Text($"{l.PricePerUnit:N0}g x{l.Quantity}{hqMark}");
                    if (_showBestServer)
                        ImGui.TextColored(Grey, l.World);
                }
            }
            ImGui.EndTable();
        }

        Dummy(4f);
        ImGui.Checkbox("Show server per listing", ref _showBestServer);
    }
}
