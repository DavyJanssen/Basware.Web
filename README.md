# Basware Web

Nederlandstalige Blazor Web App op ASP.NET Core 10 met Interactive Server, PostgreSQL/Npgsql en Linux Docker. De oorspronkelijke WPF-app wordt niet gewijzigd.

## Functies

- Orders zoeken en filteren op labo, vestiging en leverdatum, met selectie en paginering.
- Orderdetails, partners, CNK/laboherkomst en bron-XML downloaden.
- Meerdere XML-bestanden importeren met resultaten per bestand en bestaande deduplicatie.
- Expliciete CNK-labokoppelingen beheren en JSON importeren; conflicten bij JSON blijven behouden.
- Verkooporders voorbereiden met tijdelijke afdrukaantallen en opmerkingen; afdrukken of via de browser opslaan als PDF.
- Excel-overzicht van originele bestelde aantallen per labo/product/vestiging, met dezelfde controles als de desktopapp.
- Cookie-login met één configureerbaar beheeraccount, CSRF-bescherming en beperking van inlogpogingen.

## Lokaal starten (PowerShell, .NET 10 SDK)

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Authentication__Username = 'admin'
$env:Authentication__Password = Read-Host 'Kies een wachtwoord van minimaal 16 tekens' -MaskInput
$env:ConnectionStrings__Basware = Read-Host 'PostgreSQL connection string' -MaskInput
dotnet run --project src/Basware.Web --no-launch-profile --urls http://localhost:5080
```

Open http://localhost:5080/login. Zonder databaseconfiguratie verschijnt na aanmelden een configuratiemelding. Er wordt geen fictieve productie-inhoud getoond. De bestaande `EDI_CONNECTION_STRING` wordt ook ondersteund. Bewaar credentials niet in Git. Aanmelden zonder ingesteld wachtwoord is uitgeschakeld.

## Bestaande database

De services zijn overgenomen uit het aangeleverde Basware-project. Ze verwachten de bestaande `public.edi_*`-tabellen en de views `edi_order_laboratory` en `edi_line_laboratory_match`, plus de bestaande labo- en vestigingskoppelingen. Deze app voert geen schemawijzigingen of migraties uit. Gebruik dezelfde database als de desktopapp. Alleen expliciete import- en koppelacties schrijven gegevens.

## Docker met HTTPS

1. Kopieer `.env.example` naar `.env` en vul domein, databaseverbinding en een sterk wachtwoord van minstens 16 tekens in. Bij `$` in waarden: gebruik de single-quote syntaxis van Docker Compose `.env`.
2. Laat het domein naar de Docker-server wijzen; poorten 80 en 443 moeten beschikbaar zijn voor de meegeleverde Caddy-proxy.
3. Start `docker compose up -d --build` en open `https://jouw-domein/login`.
4. Bekijk logs met `docker compose logs -f web`.

De database blijft extern. `localhost` in een container is de container zelf: gebruik een vanuit Docker bereikbaar databaseadres. Pas TLS-instellingen aan het databasecertificaat aan. De meegeleverde verbinding verlangt een geldig databasecertificaat.

De proxy regelt HTTPS en WebSockets. Het Docker-netwerk gebruikt 172.30.48.0/24; kies een ander ongebruikt subnet als dit conflicteert en pas ook `ReverseProxy__Address` aan. Bij een bestaande reverse proxy: gebruik die in plaats van de meegeleverde Caddy, stuur naar poort 8080, ondersteun WebSockets en vertrouw alleen het exacte proxyadres. Publiceer de applicatiepoort niet rechtstreeks op internet. Cookies zijn in productie uitsluitend via HTTPS bruikbaar. Dataprotectiesleutels blijven in het `keys`-volume bewaard.

Deze versie gebruikt één gedeeld beheeraccount. Voor persoonlijke accounts/rollen is aansluiting op de organisatie-identiteit (bijvoorbeeld OIDC/Entra ID) een volgende uitbreiding. Blazor houdt selecties per browsersessie bij; vernieuwen of verbreken van de sessie kan tijdelijke afdrukwijzigingen wissen. Grote aantallen gelijktijdige gebruikers vragen extra schaalontwerp.

## Basware-portaal en PDF

WebView2 is Windows-specifiek en is niet meegenomen. De webapp opent het Basware-portaal in een nieuw tabblad; gedownloade XML kan via de importpagina worden verwerkt. Automatisch aanmelden/downloaden uit het portaal vereist nog een ondersteunde Basware-integratie of afzonderlijke browserautomatisering. Een browser kan de externe portaalsessie niet zomaar met deze app delen.

PDF wordt via het afdrukvenster van de browser opgeslagen; er is geen automatische server-PDF-export. De bestaande HTML-afdrukopmaak wordt hergebruikt. De Excel-export gebruikt altijd de bronhoeveelheden; tijdelijke wijzigingen op het afdrukscherm veranderen de database niet.

## Bouwen en testen

```powershell
dotnet build Basware.Web.slnx -c Release
dotnet test Basware.Web.slnx -c Release
```

`Basware.Core` bevat de platformonafhankelijke services, `Basware.Web` de webinterface en `Basware.Tests` de regressietests. De oorspronkelijke namespaces zijn behouden om de herkomst van overgenomen code duidelijk te houden. XML met DTD is in de webimport niet toegestaan.

## Uitgevoerde verificatie

- Release-build en publish: geslaagd, zonder buildwaarschuwingen.
- 29 tests: geslaagd (o.a. hoeveelheden, CNK, filters, Excel, afdrukopmaak en XML/DTD-validatie).
- Browser: aanmelden, bestaande database lezen (1.452 orders), zoekfilter, orderdetails, selectie, verkooporders en afdrukvoorbeeld gecontroleerd.
- Excel via de browser gedownload en de drie geselecteerde productaantallen gecontroleerd (10, 20 en 5).
- HTTP-controles: anonieme toegang wordt naar login gestuurd; ontbrekend CSRF-token en verkeerd wachtwoord worden geweigerd.
- Geen databasegegevens gewijzigd tijdens verificatie. Import/koppelacties zijn niet tegen de productiedatabase uitgevoerd.
- Docker-build/start nog niet uitgevoerd: Docker is niet beschikbaar op deze ontwikkelcomputer. Controleer de container op de doelserver.
- NuGet-beveiligingsaudit niet uitgevoerd tijdens de lokale build wegens een TLS-probleem in de lokale .NET-client; de ontbrekende builddependency is met certificaatvalidatie van NuGet opgehaald. Voer op de doelomgeving ook `dotnet list package --vulnerable --include-transitive` uit.
