There’s no single public dataset like CMHC’s NHA MBS pool data for uninsured mortgages. You have to piece it together. The best sources, roughly in order of how close they come:

1. Covered bond monthly investor reports (closest public match)

CMHC’s covered bond rules require every registered program (RBC, TD, BMO, Scotiabank, CIBC, National Bank, Equitable and others) to publish a monthly investor report. The cover pools must be uninsured residential mortgages.Each report includes:Pool balance and number of loansCurrent and original LTV bucketsArrears (30, 60, 90+ days)Province breakdownCredit score distributionRate type (fixed or variable)Remaining term and seasoningThey’re free on each bank’s investor relations site, and CMHC’s covered bond registry links to them. A good time series of uninsured prime pool characteristics comes from scraping these monthly.The limitation is that it’s pool-level, not loan-level, and banks pick which loans go into the pool.
2. Private-label RMBS reporting

Each deal has monthly trustee or servicer reports, with loan-level tapes for investors. They show prepayments, delinquencies, losses and pool factors, which is the most MBS-like data available.Access usually requires investor status, a data room login, or a vendor.DBRS Morningstar publishes presale and performance reports, often with pool stratifications. Moody’s and S&P cover a few deals.Vendors: Intex covers some Canadian RMBS and CMBS deals, and Bloomberg has partial coverage.
3. Bank regulatory disclosures (aggregate, every quarter)

OSFI’s B-20 guideline requires federally regulated lenders to publish insured vs. uninsured mortgage balances by province, plus average LTV on new uninsured originations. This appears in each bank’s quarterly supplementary financial information and reports.Alternative lenders such as Equitable, Home Trust and MCAN publish similar detail, including arrears, which shows the non-prime uninsured segment.
4. Market-wide aggregates

CMHC Residential Mortgage Industry Report: annual, covers insured and uninsured lending, market share by lender type, and arrears.Canadian Bankers Association: monthly 90+ day arrears for bank mortgages, split insured vs. uninsured.Bank of Canada: the Financial System Review and the financial vulnerability indicators include charts on uninsured LTV, loan-to-income and amortization. The underlying loan-level regulatory data isn’t public.Statistics Canada: residential mortgage credit outstanding by lender type.
5. Paid credit bureau data

Equifax Canada and TransUnion Canada sell delinquency and balance data by region, vintage and credit score. This is the only realistic source of performance data for the whole uninsured market across borrowers.
6. Listed MICs

Quarterly MD&A from TF, FC, AI and MKP covers loan book composition, LTV, impaired loans and geography. It covers mainly commercial and construction lending, not prime residential.
If you want something you can model with (balances, LTVs, arrears, geography over time), start with the covered bond monthly reports, which are free, monthly and fairly standardized across banks. Add the OSFI B-20 disclosures and CBA arrears for the market-wide picture. If you need prepayment speeds or loss severity, you’ll need private RMBS reports through Intex or deal data rooms, since no free public source has them.

These sources come from my training data, so check that each is still published in the same form. If it helps, I can draft a script that collects the covered bond investor reports into a single table.

MKP.TO, AI.TO, FC.TO, TF.TO
