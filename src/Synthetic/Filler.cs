namespace Overens.Synthetic;

/// <summary>
/// What most of a real agreement is about: everything that is not a pay rate. These sections
/// state none of the rules the system reads. They make a long agreement long, and they are
/// full of days, weeks, kroner and percentages that belong to something else, which is what a
/// reader of a real agreement has to see past.
/// </summary>
internal static class Filler
{
    public static readonly (string Heading, string[] Clauses)[] Employment =
    [
        ("Prøvetid og oplæring",
        [
            "I prøvetiden følger virksomheden medarbejderens oplæring og drøfter forløbet med medarbejderen mindst to gange. Virksomheden udpeger en kollega, der står for den praktiske oplæring i de første fire uger.",
            "Afbrydes ansættelsen i prøvetiden, betales løn for de timer, der er arbejdet, og optjent feriegodtgørelse afregnes ved fratrædelsen.",
            "Medarbejdere, der genansættes inden for ni måneder efter en fratræden, som ikke skyldes medarbejderens egne forhold, bevarer den anciennitet, de havde ved fratrædelsen.",
        ]),
        ("Unge under 18 år",
        [
            "Unge under 18 år må ikke beskæftiges i tidsrummet mellem kl. 20.00 og kl. 06.00 og må ikke arbejde mere end 8 timer om dagen og 40 timer om ugen.",
            "Unge under 18 år har ret til en pause på mindst 30 minutter, når den daglige arbejdstid overstiger 4 timer og 30 minutter, og til en hvileperiode på mindst 12 timer i døgnet.",
            "Unge under 18 år må ikke udføre arbejde med farlige maskiner eller stoffer og må ikke arbejde alene i tidsrummet mellem kl. 18.00 og kl. 20.00 på hverdage.",
            "Når den unge fylder 18 år, overgår den unge til overenskomstens almindelige bestemmelser fra begyndelsen af den lønperiode, hvori fødselsdagen falder.",
        ]),
        ("Ansættelsesbevis og personaleoplysninger",
        [
            "Ansættelsesbeviset skal indeholde oplysning om arbejdssted, arbejdstid, tiltrædelsestidspunkt og den løn, der er aftalt ved ansættelsen. Ændres vilkårene væsentligt, udleveres et nyt ansættelsesbevis senest en måned efter, at ændringen er trådt i kraft.",
            "Har medarbejderen ikke modtaget ansættelsesbeviset rettidigt, kan der først rejses krav om godtgørelse, når virksomheden har haft en frist på 15 dage til at rette forholdet. En godtgørelse kan ikke overstige 10.000,00 kr.",
            "Virksomheden opbevarer personaleoplysninger fortroligt. Medarbejderen har ret til at se de oplysninger, virksomheden har registreret, og til at få urigtige oplysninger rettet.",
            "Oplysninger om helbredsforhold må kun indhentes, hvis de har væsentlig betydning for medarbejderens mulighed for at udføre det pågældende arbejde.",
        ]),
        ("Deltidsansatte",
        [
            "Der kan ansættes medarbejdere på deltid med en ugentlig arbejdstid på mindst 15 timer. Arbejdstiden og dens placering aftales skriftligt ved ansættelsen.",
            "Arbejde ud over den aftalte deltid og op til 37 timer om ugen er merarbejde. Merarbejde betales med medarbejderens timeløn uden tillæg.",
            "Deltidsansatte optjener anciennitet efter samme regler som fuldtidsansatte. Deltidsansatte har fortrinsret til ledige timer i virksomheden, før der ansættes nye medarbejdere.",
        ]),
        ("Vikarer og tidsbegrænset ansættelse",
        [
            "Vikarer fra vikarbureauer, der udfører arbejde inden for overenskomstens område, er omfattet af overenskomstens bestemmelser, så længe arbejdet varer.",
            "Tidsbegrænset ansættelse kan aftales i indtil 12 måneder og kan forlænges én gang. Forlænges ansættelsen ud over 24 måneder, anses medarbejderen for ansat uden tidsbegrænsning.",
            "Virksomheden orienterer tillidsrepræsentanten om brugen af vikarer en gang i kvartalet. Orienteringen omfatter antallet af vikarer og de afdelinger, hvor de er beskæftiget.",
        ]),
    ];

    public static readonly (string Heading, string[] Clauses)[] WorkingTime =
    [
        ("Flekstid",
        [
            "Der kan lokalt aftales flekstid, hvor medarbejderen selv tilrettelægger arbejdstidens begyndelse og ophør inden for en ramme, som virksomheden fastsætter. Fikstiden skal mindst omfatte tidsrummet fra kl. 09.00 til kl. 14.00.",
            "Flekssaldoen må ved udgangen af en måned ikke overstige 15 timer i overskud eller 10 timer i underskud. Timer ud over 15 bortfalder, medmindre de er opstået efter aftale med virksomheden.",
            "Timer, der oparbejdes inden for flekstidsrammen, er ikke overarbejde og giver ikke ret til tillæg.",
        ]),
        ("Afspadsering",
        [
            "Overarbejde kan efter aftale mellem virksomheden og medarbejderen afspadseres i stedet for at blive udbetalt. Afspadseringen sker time for time, og tillægget udbetales, medmindre parterne aftaler, at også tillægget afspadseres.",
            "Afspadsering skal være afholdt inden for fire måneder efter, at overarbejdet er udført. Er afspadseringen ikke afholdt inden fristens udløb, udbetales timerne med sædvanlig overarbejdsbetaling.",
            "Tidspunktet for afspadsering varsles med mindst 72 timer. Bliver medarbejderen syg, inden arbejdstiden skulle være begyndt på afspadseringsdagen, udskydes afspadseringen.",
        ]),
        ("Arbejdstidsnedsættelse og hjemsendelse",
        [
            "Ved midlertidig mangel på arbejde kan virksomheden indføre arbejdsfordeling efter de regler, der gælder for supplerende dagpenge. Arbejdsfordeling varsles med mindst en uge og kan højst vare 13 uger.",
            "Hjemsendes medarbejderen på grund af maskinstandsning, materialemangel eller lignende forhold, som virksomheden ikke er herre over, betales løn for resten af den dag, hvor hjemsendelsen sker.",
            "Ved hjemsendelse på grund af vejrlig betales en vejrligsgodtgørelse på 82,50 kr. pr. time for indtil 7 timer og 24 minutter pr. dag, når medarbejderen har mindst tre måneders anciennitet.",
        ]),
        ("Arbejdstidens placering",
        [
            "Den normale daglige arbejdstid lægges mellem kl. 06.00 og kl. 18.00 på ugens fem første dage. Arbejdstidens begyndelse og ophør fastsættes af virksomheden efter drøftelse med tillidsrepræsentanten.",
            "Ændringer i arbejdstidens placering varsles med mindst fire uger. Ved kortere varsel betales et varskotillæg på 150,00 kr. for hver dag, varslet er forkortet, dog højst for ti dage.",
            "Der kan lokalt aftales varierende ugentlig arbejdstid, når den gennemsnitlige arbejdstid over en periode på højst 26 uger ikke overstiger 37 timer om ugen.",
            "I uger med varierende arbejdstid må den ugentlige arbejdstid ikke overstige 44 timer og den daglige arbejdstid ikke 9 timer, medmindre andet aftales lokalt.",
        ]),
        ("Pauser og hviletid",
        [
            "Ud over spisepausen har medarbejderen ret til to betalte pauser på hver 10 minutter i løbet af en fuld arbejdsdag. Pausernes placering aftales lokalt.",
            "Medarbejderen har ret til en sammenhængende hvileperiode på mindst 11 timer inden for hver periode på 24 timer. Hvileperioden kan efter lokal aftale nedsættes til 8 timer indtil to gange om ugen.",
            "Der skal i hver periode på syv døgn gives et ugentligt fridøgn, som så vidt muligt lægges på en søndag og i forlængelse af en daglig hvileperiode.",
        ]),
        ("Rådighedsvagt og tilkald",
        [
            "Medarbejdere, der efter aftale står til rådighed uden for normal arbejdstid, modtager et rådighedsbeløb på 21,50 kr. pr. time på hverdage og 43,00 kr. pr. time på lørdage, søndage og helligdage. Rådighedsbeløbet er ikke løn efter overenskomstens øvrige bestemmelser.",
            "Tilkaldes en medarbejder til arbejde under en rådighedsvagt, betales for mindst tre timer, uanset arbejdets varighed. Betalingen sker efter reglerne om overarbejde.",
            "Transporttid ved tilkald betales med timelønnen uden tillæg for indtil en time pr. tilkald.",
        ]),
        ("Weekend- og helligdagsarbejde",
        [
            "Arbejde på helligdage, der falder på en hverdag, betales med et tillæg på 100,0 pct. af timelønnen, medmindre arbejdet indgår i en fast vagtplan for holddrift.",
            "Grundlovsdag og juleaftensdag er fridage fra kl. 12.00. Nytårsaftensdag er fridag fra kl. 14.00. For timer, der bortfalder, betales sædvanlig løn.",
            "Medarbejdere, der har arbejdet mere end to weekender i træk, har ret til en arbejdsfri weekend, medmindre driftsmæssige forhold gør det umuligt.",
        ]),
    ];

    public static readonly (string Heading, string[] Clauses)[] Leave =
    [
        ("Feriens placering",
        [
            "Hovedferien på tre uger holdes i perioden fra den 1. maj til den 30. september. Virksomheden fastsætter feriens placering efter drøftelse med medarbejderen og giver meddelelse herom senest tre måneder før, ferien begynder.",
            "Den øvrige ferie holdes i sammenhængende perioder af mindst fem dages varighed, men kan placeres som enkeltdage, hvis driftsmæssige hensyn gør det ønskeligt. Varslet for øvrig ferie er en måned.",
            "Holder virksomheden lukket under ferie, kan medarbejdere, der ikke har optjent ret til ferie i alle de dage, virksomheden holder lukket, ikke kræve erstatning for de dage, der ikke er dækket af feriegodtgørelse.",
            "Ferie kan overføres til det følgende ferieår efter skriftlig aftale, der indgås inden den 31. december. Der kan højst overføres 10 feriedage i alt.",
        ]),
        ("Frihed ved kollektive fridage",
        [
            "Den 1. maj er fridag fra kl. 12.00, hvis det er muligt af hensyn til driften. For de timer, der bortfalder, betales fra medarbejderens søgnehelligdagskonto.",
            "Fredag efter Kristi himmelfartsdag kan efter lokal aftale være kollektiv fridag. Fridagen afvikles som en feriefridag eller som afspadsering.",
        ]),
        ("Feriefridage og seniorfridage",
        [
            "Feriefridage placeres efter samme regler som restferie. Feriefridage, der ikke er afholdt ved ferieårets udløb, kompenseres med et beløb svarende til 2,5 pct. af den ferieberettigede løn i kalenderåret.",
            "Fra fem år før den til enhver tid gældende folkepensionsalder kan medarbejderen indgå i en seniorordning og anvende indbetalingerne til fritvalgskontoen til finansiering af seniorfridage.",
            "Der kan højst afholdes 32 seniorfridage om året. Ønske om seniorfridage i det kommende kalenderår meddeles virksomheden senest den 1. november.",
        ]),
        ("Barns sygdom og hospitalsindlæggelse",
        [
            "Medarbejdere med mindst ni måneders anciennitet har ret til frihed med fuld løn på barnets første hele sygedag, når barnet er under 14 år og har ophold hos medarbejderen.",
            "Er barnet fortsat sygt efter den første hele sygedag, har medarbejderen ret til yderligere to fridage. Disse dage afholdes uden løn, men medarbejderen kan få udbetalt et beløb fra sin fritvalgskonto.",
            "Ved barnets hospitalsindlæggelse har medarbejderen ret til frihed med løn i indtil en uge pr. barn inden for en periode på 12 måneder. Lønnen kan højst udgøre 160,00 kr. pr. time.",
        ]),
        ("Barsel og forældreorlov",
        [
            "Virksomheden betaler løn under fravær på grund af barsel fra fire uger før forventet fødselstidspunkt og indtil ti uger efter fødslen. Lønnen svarer til den løn, medarbejderen ville have oppebåret i perioden.",
            "I forlængelse af barselsorloven ydes betaling under forældreorlov i indtil 24 uger. Af disse uger har hver forælder ret til betaling i ni uger, og de resterende seks uger ydes til den ene eller den anden forælder.",
            "Betalingen under forældreorlov kan højst udgøre 245,00 kr. pr. time. Det er en forudsætning for betalingen, at virksomheden er berettiget til refusion svarende til den maksimale dagpengesats.",
            "Under de 14 ugers barselsorlov indbetales et ekstra pensionsbidrag, som udgør 1.530,00 kr. pr. måned fra arbejdsgiveren og 765,00 kr. pr. måned fra medarbejderen. Bidraget er ikke en del af pensionsbidraget efter overenskomstens bestemmelse om pension.",
        ]),
    ];

    public static readonly (string Heading, string[] Clauses)[] Absence =
    [
        ("Sygdom under ferie og erstatningsferie",
        [
            "Bliver medarbejderen syg, før ferien begynder, har medarbejderen ikke pligt til at begynde ferien. Sygdommen skal meldes til virksomheden efter de sædvanlige regler.",
            "Bliver medarbejderen syg under ferien, har medarbejderen ret til erstatningsferie efter fem sygedage i ferieåret, når sygdommen er dokumenteret ved lægeerklæring. Medarbejderen betaler selv lægeerklæringen.",
            "Erstatningsferien holdes i samme ferieafholdelsesperiode, medmindre sygdommen varer så længe, at det ikke er muligt.",
        ]),
        ("Fastholdelse og sygefraværssamtale",
        [
            "Virksomheden indkalder en sygemeldt medarbejder til en samtale senest fire uger efter første sygedag. Samtalen handler om, hvordan og hvornår medarbejderen kan vende tilbage til arbejdet.",
            "Forventes sygefraværet at vare mere end otte uger, kan medarbejderen anmode om en fastholdelsesplan, der beskriver, hvordan medarbejderen hurtigst muligt kan vende helt eller delvist tilbage.",
            "Ved delvis raskmelding betales løn for de timer, medarbejderen arbejder, og sygeløn for de øvrige timer, så længe retten til løn under sygdom består.",
        ]),
        ("Tilskadekomst og arbejdsskader",
        [
            "Ved tilskadekomst i virksomheden betales fuld løn for de timer, medarbejderen må forsømme på tilskadekomstdagen. Virksomheden anmelder skaden efter arbejdsskadesikringslovens regler.",
            "Medarbejdere, der er fraværende på grund af en arbejdsskade, har ret til løn i indtil 12 uger, uanset anciennitet. Lønnen beregnes som medarbejderens gennemsnitlige løn i de seneste fire uger før fraværet.",
            "Ved delvis genoptagelse af arbejdet efter en arbejdsskade betales løn for de timer, der arbejdes, og sygeløn for de timer, medarbejderen fortsat er fraværende.",
        ]),
        ("Lægebesøg og anden frihed",
        [
            "Lægebesøg og lignende skal så vidt muligt lægges uden for arbejdstiden. Er det ikke muligt, gives frihed uden løn, medmindre der er tale om akut opstået sygdom.",
            "Medarbejderen har ret til frihed uden løn, når tvingende familiemæssige årsager gør medarbejderens umiddelbare tilstedeværelse påtrængende nødvendig.",
            "Ved dødsfald i den nærmeste familie har medarbejderen ret til frihed med løn på begravelsesdagen. Ved nærmeste familie forstås ægtefælle, samlever, børn og forældre.",
        ]),
        ("Børneomsorgsdage",
        [
            "Medarbejdere med mindst ni måneders anciennitet, som har ret til at holde barns første sygedag, har ret til to børneomsorgsdage pr. ferieår, uanset hvor mange børn medarbejderen har.",
            "Børneomsorgsdagene afholdes uden løn, men medarbejderen kan få udbetalt et beløb fra sin fritvalgskonto. Dagene placeres efter aftale mellem virksomheden og medarbejderen.",
        ]),
    ];

    public static readonly (string Heading, string[] Clauses)[] Cooperation =
    [
        ("Faglig klub og møder",
        [
            "Medarbejderne kan oprette en faglig klub. Klubbens bestyrelse kan holde indtil fire møder om året i arbejdstiden, hvert af højst en times varighed, uden fradrag i lønnen.",
            "Virksomheden stiller et egnet lokale til rådighed for klubbens møder og giver tillidsrepræsentanten adgang til en aflåselig skabsplads, telefon og internetforbindelse.",
            "Nyansatte medarbejdere har ret til 30 minutters betalt frihed til en introduktion ved tillidsrepræsentanten inden for den første måned af ansættelsen.",
        ]),
        ("Informations- og høringspligt",
        [
            "Virksomheden informerer medarbejderne om forhold, der har væsentlig betydning for beskæftigelsen, så tidligt at medarbejdernes synspunkter kan indgå i grundlaget for beslutningen.",
            "Ved afskedigelser af større omfang indleder virksomheden forhandlinger med tillidsrepræsentanten senest 30 dage, før opsigelserne afgives. Ved afskedigelse af mindst 50 pct. af medarbejderne i en virksomhed med over 100 ansatte er fristen otte uger.",
            "Medarbejdere, der afskediges som led i afskedigelser af større omfang, har ret til frihed med løn i indtil to timer til at søge vejledning i deres a-kasse.",
        ]),
        ("Samarbejdsudvalg",
        [
            "I virksomheder, der beskæftiger 35 medarbejdere eller flere, oprettes et samarbejdsudvalg, hvis enten virksomheden eller et flertal af medarbejderne ønsker det. Udvalget består af lige mange repræsentanter for ledelsen og medarbejderne.",
            "Samarbejdsudvalget holder ordinært møde seks gange om året. Dagsordenen udsendes senest otte dage før mødet, og referatet udsendes senest 14 dage efter mødet.",
            "Samarbejdsudvalget orienteres om virksomhedens økonomiske situation og fremtidsudsigter, herunder ordrebeholdning, produktionsforhold og beskæftigelsesforhold.",
        ]),
        ("Arbejdsmiljø",
        [
            "Parterne er enige om, at et sikkert og sundt arbejdsmiljø har betydning for både medarbejdernes trivsel og virksomhedens konkurrenceevne. Arbejdsmiljøarbejdet tilrettelægges i samarbejde mellem ledelsen og medarbejderne.",
            "Arbejdsmiljørepræsentanten vælges for to år ad gangen og er beskyttet mod afskedigelse på samme måde som tillidsrepræsentanten. Arbejdsmiljørepræsentanten har ret til den nødvendige uddannelse i arbejdstiden.",
            "Virksomheden stiller de personlige værnemidler til rådighed, som arbejdet kræver. Sikkerhedsfodtøj udleveres efter behov, og virksomheden betaler indtil 900,00 kr. pr. par.",
            "Ved arbejde i kølerum og fryserum med en temperatur under 5 grader betales et kuldetillæg på 9,25 kr. pr. time for den tid, arbejdet varer. Kuldetillægget er ikke omfattet af bestemmelserne om tillæg for forskudt tid.",
        ]),
        ("Efteruddannelse og kompetenceudvikling",
        [
            "Medarbejdere med mindst seks måneders anciennitet har ret til en systematisk uddannelsesplanlægning. Planen drøftes mellem medarbejderen og den nærmeste leder en gang om året.",
            "Under selvvalgt uddannelse yder parternes kompetencefond et tilskud, der svarer til 85,0 pct. af den sædvanlige overenskomstmæssige løn. Tilskuddet kan højst udgøre 175,00 kr. pr. time.",
            "Medarbejdere, der afskediges på grund af omstruktureringer, nedskæringer eller andre forhold hos virksomheden, har ret til at deltage i et kursus af indtil to ugers varighed med tilskud fra kompetencefonden.",
        ]),
        ("Tillidsrepræsentantens vilkår",
        [
            "Tillidsrepræsentanten modtager et årligt vederlag fra parternes fond. Vederlaget udgør 9.000,00 kr. ved et valggrundlag på indtil 49 medarbejdere, 16.500,00 kr. ved 50 til 99 medarbejdere og 33.000,00 kr. ved 100 medarbejdere eller flere.",
            "En tillidsrepræsentant kan ikke afskediges, uden at der foreligger tvingende årsager. Virksomheden skal rette henvendelse til sin organisation, inden afskedigelsen iværksættes.",
            "En medarbejder, der ophører som tillidsrepræsentant efter mindst et års virke, har krav på seks ugers opsigelsesvarsel ud over sit individuelle varsel, hvis medarbejderen opsiges inden for et år efter ophøret.",
        ]),
    ];

    public static readonly (string Heading, string[] Clauses)[] Other =
    [
        ("Fratrædelsesgodtgørelse",
        [
            "Medarbejdere, der afskediges efter mindst tre års uafbrudt ansættelse uden at være skyld heri, modtager en fratrædelsesgodtgørelse. Godtgørelsen udgør 5.000,00 kr. efter tre års ansættelse, 10.000,00 kr. efter seks år og 15.000,00 kr. efter otte år.",
            "Godtgørelsen udbetales ikke, hvis medarbejderen ved fratrædelsen har fået andet arbejde, modtager pension eller af andre grunde ikke modtager dagpenge.",
            "Deltidsansatte modtager en forholdsmæssig andel af godtgørelsen, beregnet efter den aftalte ugentlige arbejdstid.",
        ]),
        ("Elektroniske dokumenter",
        [
            "Virksomheden kan med frigørende virkning aflevere lønsedler, feriekort og andre dokumenter, der udveksles under eller efter ansættelsen, i de elektroniske postløsninger, som er til rådighed.",
            "Ønsker virksomheden at benytte denne mulighed, varsles medarbejderne herom med tre måneders varsel, medmindre andet aftales. Efter varslets udløb kan medarbejdere, der ikke har adgang til en elektronisk postløsning, få udleveret dokumenterne ved henvendelse til virksomheden.",
        ]),
        ("Sociale klausuler og fleksjob",
        [
            "Parterne opfordrer virksomhederne til at ansætte medarbejdere med nedsat arbejdsevne. Der kan indgås aftale om ansættelse på særlige vilkår, når det sker i forståelse med tillidsrepræsentanten.",
            "For medarbejdere i fleksjob aftales løn og arbejdstid under hensyn til den enkeltes arbejdsevne. Lønnen kan dog ikke aftales lavere end 121,00 kr. pr. time, og aftalen skal godkendes af organisationerne.",
            "Medarbejdere i virksomhedspraktik og løntilskudsjob tæller ikke med ved opgørelsen af, om virksomheden har det antal ansatte, der giver ret til at vælge tillidsrepræsentant.",
        ]),
        ("Arbejdsgiverens indbetalinger til fonde",
        [
            "Til parternes udviklingsfond indbetaler virksomheden 45 øre pr. præsteret arbejdstime. Beløbet opkræves sammen med bidraget til uddannelsesfonden.",
            "Til den særlige fond for tillidsrepræsentanters uddannelse indbetaler virksomheden 3 øre pr. præsteret arbejdstime.",
            "Bidragene til fondene er ikke løn og giver ikke medarbejderen noget krav mod virksomheden.",
        ]),
        ("Arbejdstøj",
        [
            "Virksomheden stiller arbejdstøj til rådighed, hvor arbejdets art gør det nødvendigt. Arbejdstøjet er virksomhedens ejendom og afleveres ved fratræden.",
            "Hvor virksomheden ikke stiller arbejdstøj til rådighed, ydes en tøjgodtgørelse på 185,00 kr. pr. måned. Godtgørelsen er ikke løn og indgår ikke i beregningen af feriegodtgørelse eller pension.",
            "Virksomheden sørger for vask og vedligeholdelse af det udleverede arbejdstøj. Beskadiges arbejdstøjet ved medarbejderens grove uagtsomhed, kan virksomheden kræve det erstattet.",
        ]),
        ("Rejse og befordring",
        [
            "Sendes medarbejderen til arbejde uden for virksomhedens faste arbejdssted, betaler virksomheden rejseudgifterne. Ved kørsel i egen bil ydes godtgørelse efter statens til enhver tid gældende satser.",
            "Rejsetid uden for normal arbejdstid betales med 75,0 pct. af timelønnen. Rejsetid medregnes ikke ved opgørelsen af overarbejde.",
            "Ved arbejde, der medfører overnatning, ydes time- og dagpenge efter de satser, der gælder for skattefri rejsegodtgørelse. Virksomheden betaler desuden logi efter regning.",
        ]),
        ("Lønudbetaling",
        [
            "Lønperioden er kalendermåneden. Lønnen er til disposition på medarbejderens konto den sidste bankdag i måneden. Tillæg, der ikke kan opgøres inden lønkørslen, udbetales sammen med lønnen for den følgende måned.",
            "Lønsedlen sendes til medarbejderens digitale postkasse. Af lønsedlen skal det fremgå, hvordan lønnen er beregnet, herunder antallet af timer og de tillæg, der er betalt.",
            "Er der udbetalt for meget i løn ved en fejl, som medarbejderen burde have indset, kan virksomheden modregne beløbet i de følgende lønudbetalinger med højst 15,0 pct. af nettolønnen pr. måned.",
        ]),
        ("Lokalaftaler",
        [
            "Der kan indgås lokalaftaler mellem virksomheden og tillidsrepræsentanten om forhold, som overenskomsten giver adgang til at fravige. Lokalaftaler skal være skriftlige.",
            "Lokalaftaler kan opsiges med to måneders varsel til udgangen af en måned, medmindre der er aftalt et længere varsel. Efter opsigelse er parterne forpligtet til at overholde aftalen, indtil en anden aftale træder i stedet eller overenskomstperioden udløber.",
            "En lokalaftale kan ikke fastsætte en lavere betaling end overenskomstens satser. Hvor en lokalaftale giver en højere timeløn end overenskomstens mindstebetaling, gælder lokalaftalens sats for de medarbejdere, den omfatter.",
        ]),
        ("Organisationsforhold",
        [
            "Overenskomstens parter anbefaler, at medarbejderne er medlem af det forbund, der er part i overenskomsten. Ingen kan dog pålægges at være medlem af en bestemt organisation.",
            "Virksomheden kan ikke i overenskomstperioden træffe aftale med andre organisationer om løn- og arbejdsforhold inden for overenskomstens område.",
            "Udmelder virksomheden sig af arbejdsgiverforeningen, er den bundet af overenskomsten, indtil denne udløber efter opsigelse.",
        ]),
        ("Ligebehandling og chikane",
        [
            "Parterne er enige om at fremme ligebehandling på arbejdspladsen og at modvirke enhver form for forskelsbehandling på grund af køn, alder, handicap, race, religion, seksuel orientering eller etnisk oprindelse.",
            "Virksomheden skal sikre, at medarbejderne ikke udsættes for krænkende handlinger, herunder mobning og seksuel chikane. Virksomheden fastlægger en politik herom i samarbejde med medarbejderne.",
            "Uoverensstemmelser om ligeløn kan behandles i et nævn, som parterne har nedsat. Nævnet kan tilkende en godtgørelse, der fastsættes efter de regler, der gælder for sager om usaglig afskedigelse.",
        ]),
        ("Afskedigelsesnævn",
        [
            "Hævder en medarbejder med mindst ni måneders anciennitet, at en afskedigelse er urimelig og ikke begrundet i medarbejderens eller virksomhedens forhold, kan sagen behandles lokalt mellem virksomheden og tillidsrepræsentanten.",
            "Opnås der ikke enighed lokalt, kan sagen indbringes for et afskedigelsesnævn. Klagen skal være nævnet i hænde senest syv uger efter, at afskedigelsen er meddelt.",
            "Finder nævnet afskedigelsen urimelig, kan det tilkende medarbejderen en godtgørelse, der ikke kan overstige 52 ugers løn.",
        ]),
    ];
}
