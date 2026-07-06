# გამოშვების სახელმძღვანელო

Stenor ვრცელდება Velopack ინსტალერით (`Setup.exe`), განახლების ფიდი კი GitHub
Releases-ია: დაინსტალირებული აპი გაშვებისას ფონურად ამოწმებს
<https://github.com/giorgi02/Stenor>-ის რელიზებს, ახალ ვერსიას ჩუმად ჩამოტვირთავს და
**შემდეგ გაშვებაზე** აყენებს (`App.xaml.cs` → `CheckForUpdatesInBackground`).

## ერთჯერადი მომზადება

```powershell
dotnet tool install -g vpk --version 1.2.0   # Velopack CLI — ვერსია ემთხვევა csproj-ის Velopack პაკეტს
```

## ახალი ვერსიის გამოშვება

1. **ვერსიის აწევა** — `src/Stenor.App/Stenor.App.csproj`-ში შეცვალე `<Version>` (SemVer).

2. **(არასავალდებულო) წინა რელიზის ჩამოტვირთვა**, რომ vpk-მ პატარა delta პაკეტიც ააწყოს:

   ```powershell
   vpk download github --repoUrl https://github.com/giorgi02/Stenor -o releases
   ```

3. **აწყობა** — აქვეყნებს აპს და კრავს ინსტალერს:

   ```powershell
   pwsh scripts/pack.ps1
   ```

   შედეგი `releases/`-ში: `Stenor-win-Setup.exe`, `Stenor-X.Y.Z-full.nupkg`
   (+ `-delta.nupkg`, თუ მე-2 ნაბიჯი გაიარე), `releases.win.json`, `Stenor-win-Portable.zip`.

   > vpk უარს ამბობს, თუ `releases/`-ში იგივე ან უფრო მაღალი ვერსია უკვე დევს — ან
   > ვერსია აწიე, ან საქაღალდე გაასუფთავე.

4. **ხელით შემოწმება** — გაუშვი `releases/Stenor-win-Setup.exe`, გაიარე
   [manual-checklist.md](manual-checklist.md); ინსტალაცია ჯდება `%LocalAppData%\Stenor`-ში
   (Start Menu-ს მალსახმობი + Apps-ში ჩანაწერი). წაშლა: Settings → Apps → Stenor, ან
   `%LocalAppData%\Stenor\Update.exe --uninstall`. მომხმარებლის პარამეტრებს
   (`%APPDATA%\Stenor`) წაშლა არ ეხება.

5. **GitHub Release-ზე ატვირთვა** — ამის შემდეგ დაინსტალირებული აპები თავად განახლდებიან:

   ```powershell
   vpk upload github --repoUrl https://github.com/giorgi02/Stenor `
       --tag vX.Y.Z --releaseName "Stenor X.Y.Z" --publish `
       --token <GitHub PAT> -o releases
   ```

   რეპო საჯარო უნდა იყოს, რომ დაინსტალირებულმა აპებმა ფიდი ტოკენის გარეშე წაიკითხონ.

## ფიდის გადაფარვა ტესტისთვის

`%APPDATA%\Stenor\settings.json`-ში `UpdateFeedUrl`-ს მიეცი ალტერნატიული ფიდი (ლოკალური
საქაღალდე ან URL); ცარიელი მნიშვნელობა = ჩაშენებული GitHub ფიდი. UI-ში ეს ველი განზრახ
არ ჩანს — მხოლოდ ხელით რედაქტირებისთვისაა.

## ცნობილი შეზღუდვები

- ინსტალერი **ხელმოუწერელია** (code-signing სერტიფიკატი არ გვაქვს) — პირველ გაშვებაზე
  SmartScreen-ის გაფრთხილება მოსალოდნელია („More info" → „Run anyway").
- განახლება მხოლოდ აპის მომდევნო გაშვებისას ედება; რადგან Stenor ტრეიში მუდმივად ზის,
  პრაქტიკულად ეს ნიშნავს — Quit-ისა და ხელახლა გაშვების (ან სისტემის გადატვირთვის) შემდეგ.
