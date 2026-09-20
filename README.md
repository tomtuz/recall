# Recall

**Recall** – internetinė aplikacija mokymosi procesui palengvinti, pritaikant
pasikartojančiu intervalu rodomas klausimų ir atsakymų korteles
(spaced repetition).

## Sistemos paskirtis

Projekto tikslas – sukurti internetinę aplikaciją „Recall", skirtą mokymosi
proceso palengvinimui pritaikant pasikartojančiu intervalu rodomas klausimų
ir atsakymų korteles.

Veikimo principas – naudotojas sukuria kortelių sąrašą ir jį pasirinktinai
priskiria kortelių kategorijai. Kortelių sąraše naudotojas suskirsto mokomąją
medžiagą į klausimų ir atsakymų korteles. Peržiūrėdamas korteles, naudotojas
mygtukais įvertina, kaip sekėsi prisiminti atsakymus. Pagal naudotojo
įvertinimus sistema nustato kiekvienos kortelės pakartotinės peržiūros laiką.

## Funkciniai reikalavimai

Neregistruotas naudotojas gali:

1. Peržiūrėti viešus kortelių sąrašus, jų kategorijas ir korteles.
2. Ieškoti viešai paskelbtų kortelių sąrašų, juos filtruoti pagal kategorijas.
3. Užsiregistruoti sistemoje.
4. Prisijungti prie sistemos.

Registruotas naudotojas gali:

1. Naudotis visomis neregistruoto naudotojo peržiūros ir paieškos funkcijomis.
2. Kurti, peržiūrėti, redaguoti ir šalinti savo kortelių sąrašus.
3. Kurti, peržiūrėti, redaguoti ir šalinti savo kortelių sąrašų kategorijas.
4. Kurti, peržiūrėti, redaguoti ir šalinti korteles savo kortelių sąrašuose.
5. Importuoti kortelių rinkinį JSON formatu.
6. Pradėti mokymosi sesiją ir ją kartoti.
7. Mygtukais įvertinti, kaip sekėsi prisiminti kiekvienos kortelės atsakymą.
8. Peržiūrėti korteles, kurioms jau laikas pakartotinei peržiūrai, peržiūrėti
   asmeninę kortelių įsisavinimo suvestinę.
9. Atsijungti nuo sistemos.
10. Keisti savo kortelių sąrašų matomumą.

Administratorius gali:

1. Naudotis visomis registruotam naudotojui prieinamomis funkcijomis.
2. Peržiūrėti ir redaguoti visų naudotojų kortelių sąrašus, jų kategorijas
   ir korteles.
3. Šalinti netinkamą naudotojų sukurtą turinį.
4. Keisti kortelių sąrašų matomumą.

## Sistemos architektūra

Aplikaciją sudaro šios dalys:

- **Naudotojo aplikacija (frontend)** – naršyklėje veikianti aplikacija,
  sukurta naudojant „React" technologiją. Joje naudotojas tvarko mokymosi
  medžiagą, kartoja korteles ir peržiūri kartojimo statistiką.
- **Serverio aplikacija (backend)** – aplikacija, sukurta naudojant
  „ASP.NET Core", kuri apdoroja užklausas per programavimo sąsają (API),
  tikrina prieigos teises ir planuoja kortelių kartojimą pagal supaprastintą
  „SuperMemo" SM-2 algoritmą.
- **Duomenų bazė** – „PostgreSQL", kurioje saugomi sistemos duomenys.
  Duomenų bazės operacijoms naudojamas „Entity Framework Core".


„React" naudotojo aplikaciją ir „ASP.NET Core" serverio aplikaciją numatoma įdiegti
viename „Azure Container App" serveryje, todėl abi aplikacijos bus pasiekiamos tuo
pačiu HTTPS adresu. Naudotojo naršyklė HTTPS protokolu atsisiunčia „React"
aplikaciją, o vėliau siunčia „/api/v1" užklausas „ASP.NET Core" API. Duomenys
saugomi atskiroje „Azure Database for PostgreSQL" duomenų bazėje, kurią API
pasiekia per ORM sąsają.

## Diegimo būsena

Planuojama – naudoti „Azure for Students" aplinką („Azure Container App" +
„Azure Database for PostgreSQL Flexible Server"). Faktinis URL bus įrašytas
po realaus diegimo.