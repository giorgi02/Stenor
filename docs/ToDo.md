# გასაკეთებელი

## 1. Focus-ის დაცვა და უსაფრთხო ჩასმა

ახლა ტექსტი ტრანსკრიფციის დასრულების მომენტში აქტიურ ფანჯარაში იგზავნება. თუ მომხმარებელი ამ დროს სხვა პროგრამაზე გადავიდა, ტექსტი შეიძლება არასწორ ადგილას ჩაჯდეს. იგივე პრობლემა Live typing-შიც არსებობს.

სასურველია:

- ჩაწერის დაწყებისას აქტიური ფანჯრის დამახსოვრება;
- დასრულებისას focus-ის ცვლილების აღმოჩენა;
- ასეთ შემთხვევაში ტექსტის ავტომატურად ჩასმის ნაცვლად: „Focus changed — click to paste“;
- Live რეჟიმში ფანჯრის შეცვლისას pause ან warning.

ეს ყველაზე მნიშვნელოვანი reliability გაუმჯობესებაა. ამჟამინდელი interface მხოლოდ მიმდინარე focus-ში injection-ს ითვალისწინებს: [ITextInjector.cs](D:/GitHub/Stenor/Stenor/src/Stenor.Core/Interfaces/ITextInjector.cs:7).

## 6. ტრანსკრიფციის სტილები

ერთი ფიქსირებული „clean verbatim“ რეჟიმის ნაცვლად:

- Exact — მაქსიმალურად სიტყვასიტყვითი;
- Clean — არსებული ქცევა;
- Email — გამართული წერილის ფორმატი;
- Message — მოკლე, არაფორმალური;
- Code/Technical — ტერმინებისა და პუნქტუაციის განსაკუთრებული დამუშავება;
- Custom instructions.

სტილის სწრაფი შეცვლა შესაძლებელი იქნება tray-დან ან ცალკე hotkey-ით. შესაბამისი პარამეტრები ამჟამინდელ settings model-ში არ არსებობს: [AppSettings.cs](D:/GitHub/Stenor/Stenor/src/Stenor.Core/Models/AppSettings.cs:11).

## 7. განახლების უკეთესი UX

განახლება ახლა ფონურად იტვირთება და მხოლოდ log-ში იწერება, რომ შემდეგ გაშვებაზე დაყენდება: [App.xaml.cs](D:/GitHub/Stenor/Stenor/src/Stenor.App/App.xaml.cs:247).

სასურველია tray-ში:

- “Update ready — Restart Stenor”;
- მიმდინარე ვერსიის ჩვენება;
- Check for updates;
- release notes-ის ბმული.

ასევე installer-ისა და executable-ის code signing მნიშვნელოვნად შეამცირებს SmartScreen-ის გაფრთხილებით გამოწვეულ უნდობლობას.

## 8. პირველი გაშვების wizard

ამჟამინდელი პირველი გაშვება პირდაპირ Settings ფანჯარას ხსნის. უკეთესი onboarding იქნება:

1. API key;
2. Test connection;
3. Microphone test;
4. Hotkey selection;
5. საცდელი dictation;
6. „Everything works“ შედეგი.

ეს მნიშვნელოვნად შეამცირებს შემთხვევებს, როცა მომხმარებელი ვერ ხვდება პრობლემა microphone-შია, key-ში, ქსელში თუ hotkey-ში.
