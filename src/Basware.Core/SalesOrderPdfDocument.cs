using System.Net;
using System.Text;

namespace WpfAppBaswareLogin.Services;

public static class SalesOrderPdfDocument
{
    public static string BuildHtml(IEnumerable<SalesOrderPrintData> orders)
    {
        var sorted = SalesOrderPrintData.Sort(orders);
        if (sorted.Count == 0) throw new ArgumentException("Selecteer minstens één order.", nameof(orders));
        var html = new StringBuilder("""
            <!doctype html><html lang="nl"><head><meta charset="utf-8">
            <title>Verkooporders</title><style>
            @page { size: A4 portrait; margin: 10mm; }
            * { box-sizing: border-box; }
            body { margin: 0; font: 12px 'Segoe UI', Arial, sans-serif; color: #172433; }
            .page { width: 190mm; height: 276mm; position: relative; break-after: page; }
            .page:last-child { break-after: auto; }
            .sheet { width: 190mm; border: 1px solid #d8dfe8; padding: 18px; transform-origin: top left; }
            .top { display: grid; grid-template-columns: 1fr 220px; gap: 20px; margin-bottom: 18px; }
            .details { display: grid; grid-template-columns: 1.3fr 1fr 1fr; gap: 20px; margin-bottom: 18px; }
            .label { font-weight: 700; margin-bottom: 4px; }
            .value { white-space: pre-wrap; overflow-wrap: anywhere; }
            .spaced { margin-top: 8px; }
            table { border-collapse: collapse; width: 100%; table-layout: fixed; }
            th, td { border: 1px solid #d8dfe8; padding: 7px 6px; text-align: left; vertical-align: top; white-space: pre-wrap; overflow-wrap: anywhere; }
            th { background: #edf1f5; font-weight: 600; }
            tbody tr:nth-child(even) { background: #f7f9fc; }
            </style></head><body>
            """);
        foreach (var order in sorted)
        {
            html.Append("<section class=\"page\"><div class=\"sheet\"><div class=\"top\"><div>");
            Field("Klant", order.ClientName);
            html.Append("</div><div>");
            Field("Ordernummer", order.PrintOrderNumber);
            html.Append("<div class=\"spaced\">");
            Field("Orderdatum", order.PrintOrderDate);
            html.Append("</div></div></div><div class=\"details\"><div>");
            Field("Vestiging (volledige naam)", order.PrintDeliveryPoint);
            html.Append("</div><div>");
            Field("Gevraagde levering", order.PrintRequestedDelivery);
            html.Append("</div><div>");
            Field("Labo", order.PrintLaboratory);
            html.Append("</div></div><table><colgroup><col style=\"width:17%\"><col style=\"width:40%\"><col style=\"width:13%\"><col style=\"width:30%\"></colgroup><thead><tr><th>CNK</th><th>Omschrijving</th><th>Aantal</th><th>Opmerking</th></tr></thead><tbody>");
            foreach (var line in order.Lines)
            {
                html.Append("<tr>");
                foreach (var value in new[] { line.CnkDisplay, line.Description, line.Quantity, line.Comment })
                    html.Append("<td>").Append(WebUtility.HtmlEncode(value)).Append("</td>");
                html.Append("</tr>");
            }
            html.Append("</tbody></table></div></section>");
        }
        html.Append("</body></html>");
        return html.ToString();

        void Field(string label, string value) => html.Append("<div class=\"label\">").Append(label)
            .Append("</div><div class=\"value\">").Append(WebUtility.HtmlEncode(value)).Append("</div>");
    }

    // Measure the complete order, including all rows. Reduce only when needed to fit one page.
    public const string FitPagesScript = """
        (() => {
            for (const page of document.querySelectorAll('.page')) {
                const sheet = page.querySelector('.sheet');
                const bounds = sheet.getBoundingClientRect();
                const height = Math.max(Math.ceil(bounds.height), sheet.scrollHeight + 2);
                const width = Math.max(Math.ceil(bounds.width), sheet.scrollWidth + 2);
                const scale = Math.min(1, (page.clientHeight - 1) / height, (page.clientWidth - 1) / width);
                sheet.style.transform = `scale(${scale})`;
                // Prevent the unscaled layout box from causing extra printed pages.
                page.style.overflow = 'hidden';
            }
            return document.querySelectorAll('.page').length;
        })()
        """;
}
