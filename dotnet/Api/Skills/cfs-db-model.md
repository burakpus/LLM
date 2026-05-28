---
name: CFS DB Model Assistant
description: Kredi/finans domain SQL sorguları, şema bilgisi ve raporlama (Quotation, qtPaymentPlan, qtPaymentReceipt, vergi alanları KKDF/BMSV, GL accounting)
icon: database
---

# SQL Skill Reference - Credit/Loan Domain

Standalone reference for writing reports and queries without needing source SQL files.
Covers schema, column semantics, code dictionaries, formulas, blueprints, and helpers.

---

## Domain Overview

This schema models the full lifecycle of a **consumer/commercial credit (loan)**:
origination → payment plan → payment collection → GL accounting → status management.

Core layers:
1. **Credit** — `Quotation`, `CreditStatus`
2. **Payment Schedule** — `qtPaymentPlan`, `qtPaymentPlanDetail`, `qtPaymentPlanCalcDetails`
3. **Collections** — `qtPaymentReceipt`, `qtPaymentAlloc`, `qtPaymentAllocDetail`, `qtPaymentAllocCancellation`
4. **Accounting** — `PaymentVoucherDetail`, `h_gl`
5. **Collateral / Security** — `qtSecurity`, `CustomerSecurityDefinitions`, `VehicleVIN`, `VehicleVINEGM`
6. **Products** — `qtEndUserProducts`, `EndUserProduct`, vehicle hierarchy tables, `service`, `srvprovider`
7. **Rotating Credit Accrual** — `Interest`, `InterestDetail`
8. **Reference / Lookup** — `Dealer`, `Proposal`, `ProposalRole`, `ProductType`, `CFS_SystemParams`

---

## Table Catalog with Column Definitions

### Quotation
Primary credit/application record. Every credit starts here.

| Column | Type | Description |
|---|---|---|
| `BaseID` | int | Primary key. Used as foreign key by all linked tables |
| `qtQuoteCode` | varchar | Human-readable credit number (e.g. `'223036'`, `'S14799'`) |
| `qtClientNationalID` | varchar | Customer national ID (TCKN for individuals, tax ID for companies) |
| `qtClientName` | varchar | Customer full name |
| `qtCreditTypeCode` | varchar | Credit type: `AK`=Auto Loan, `KK`=Mortgage, `FK`=Consumer Loan, `IF`=?, `DG`=? |
| `qtCustTypeOfUseID` | int | Customer usage type: `1`=Personal, `2`=Commercial |
| `qtCustTypeDetailID` | int | Detailed customer type sub-code |
| `qtAmttoFinance` | money | Loan principal amount |
| `qtVehicleFinanceAmount` | money | Financed amount attributed to main product(s) |
| `qtServicesPrice` | money | Financed amount attributed to side products/services |
| `qtCashDownpayment` | money | Total customer cash down payment |
| `qtPeriod` | int | Number of installments (term in months) |
| `qtQuotationDate` | datetime | Application/origination date |
| `qtFleetCode` | varchar | Fleet identifier if applicable |
| `ppMonthlyInt` | decimal | Base monthly interest rate (%) |
| `ppMonthlyIntCalc` | decimal | Campaign/calculated monthly interest rate |
| `qtBMSV` | decimal | BSMV tax rate (%) applied on interest |
| `qtKKDF` | decimal | KKDF fund rate (%) — may be 0 for commercial |
| `qtCurrencyCode` | varchar | Currency code (e.g. `TRY`, `USD`, `EUR`) |
| `qtIndexTypeCode` | varchar | Index type for FX-indexed credits |
| `qtExactInvoiceAmount` | money | Exact invoice amount of the product |
| `qtExactInvoiceAmountOrg` | money | Original invoice amount |
| `qtVehicleVINID` | int | FK to `VehicleVIN.BaseID` for primary vehicle |
| `qtVehModelYearID` | int | FK to `vehModelYear.BaseID` |
| `qtDealerID` | int | FK to `Dealer.BaseID` |
| `qtCommercialProductID` | int | Campaign / commercial product reference (used as `CampaignCode` in reports) |

---

### CreditStatus
One active status row per credit at any time. Tracks lifecycle state.

| Column | Type | Description |
|---|---|---|
| `BaseID` | int | Primary key |
| `csQuotationID` | int | FK to `Quotation.BaseID` |
| `csCreditCode` | varchar | Status code: `VA`=Active, `LI`=Liquidation, `GD`=Definitive Management, `END`=Closed |
| `csContractStatusCode` | varchar | Contract sub-status: `FANT`=Early closure |
| `csCreditOpeningLastUpdate` | datetime | Credit opening/activation date |
| `csQuotationStatusLastUpdate` | datetime | Last status change date (used as close date when `END`) |
| `csRefundRequestDate` | datetime | Cancellation request date if applicable |
| `csPropStatusName_mlg` | int | FK to multilingual status label (`getDataFromLangId`) |

---

### qtPaymentPlan
Payment plan header. One credit can have multiple plans (e.g. restructured credits).

| Column | Type | Description |
|---|---|---|
| `BaseID` | int | Primary key |
| `ppQuotationID` | int | FK to `Quotation.BaseID` |
| `ppActivationDate` | date | Plan activation / first payment calculation date |
| `ppLastInstallmentDate` | date | Final maturity date |
| `ppProductTypeID` | int | Product type: `1`=FIXED, `2`=BALLOON, `3`=MULTISTEP, `10`=SPOT, `19`=ROTATIF, `37`=GRACE, `38`=PERIODIC |
| `ppStructureTypeID` | int | Structure: `0`=Normal, `1`=Structured/restructured |
| `ppCreationType` | varchar | How plan was created |
| `ppRestructured` | bit | Restructuring flag |
| `ppRestructuringDate` | date | Date of restructuring |
| `ppTransferToNormal` | bit | Returned to normal status flag |
| `ppInterestAccrualPeriod` | int | Accrual period for rotating credits |
| `ppCutDate` | date | Date credit entered collection/follow-up (null = not in follow-up) |
| `ppCutDPD` | int | Snapshot: days past due at cut date |
| `ppCutMinAssumedDueDate` | date | First overdue installment that triggered cut |
| `ppCutSourceQuotationID` | int | FK to source `Quotation.BaseID` if this plan came from restructuring |
| `ppCutCapitalAmount` | money | Outstanding principal at cut date |
| `ppDescription` | varchar | Free-text notes on plan modifications |

---

### qtPaymentPlanDetail
One row per installment per plan. The most query-intensive table in the schema.

| Column | Type | Description |
|---|---|---|
| `BaseID` | int | Primary key |
| `pPaymentPlanID` | int | FK to `qtPaymentPlan.BaseID` |
| `pPaymentPlanType` | int | Component type — see code dictionary below |
| `ppPaymentAmountRowIndex` | int | Installment sequence number (1-based) |
| `ppAssumedDueDate` | date | Scheduled due date |
| `ppPaymentRef` | varchar | Installment reference code |
| `ppAccuralDate` | date | Date this row was recognized/accrued in GL |
| `ppInstallmentIsCLOSED_FL` | bit | `1` = fully paid, `0` = open |
| `ppInstallmentClosedDate` | datetime | Actual payment/close date |
| `ppIsPayable` | bit | Whether this installment is currently payable |
| `ppdRowCreateTypeCode` | varchar | Row origin: `LT`=follow-up/cut, `ES`=early closure |
| `ppdFeeType` | int | Fee subtype: `4`=opening fee |
| `ppSourceID` | int | FK to source record (e.g. `FeeDefinition`) |
| `ppQtServiceID` | int | FK to service record |
| `ppLateIntAllocID` | int | FK to late interest allocation |
| — | — | **Financial columns** |
| `ppCapitalAmount` | money | Principal scheduled for this installment |
| `ppCapitalAmountCLOSED` | money | Principal collected |
| `ppRemainingCapitalAmount` | money | Outstanding principal (snapshot) |
| `ppInterestAmount` | money | Interest accrued for this installment |
| `ppInterestAmountCLOSED` | money | Interest collected |
| `ppInterestAmountDISCOUNT` | money | Interest discount calculated (not yet applied) |
| `ppInterestAmountDISCOUNTCLOSED` | money | Interest discount applied |
| `ppInterestAmountTODAY` | money | Interest remaining payable as of today |
| `ppEffectiveInterestAmount` | money | Effective interest amount for IRR calc |
| `ppBMSV` | money | BSMV tax accrued |
| `ppBSMVCLOSED` | money | BSMV tax collected |
| `ppBSMVDISCOUNT` | money | BSMV discount calculated |
| `ppBSMVDISCOUNTCLOSED` | money | BSMV discount applied |
| `ppBSMVTODAY` | money | BSMV remaining payable today |
| `ppKKDF` | money | KKDF fund accrued |
| `ppKKDFCLOSED` | money | KKDF collected |
| `ppKKDFDISCOUNT` | money | KKDF discount calculated |
| `ppKKDFDISCOUNTCLOSED` | money | KKDF discount applied |
| `ppKKDFTODAY` | money | KKDF remaining payable today |
| `ppTotalPayment` | money | Total due: capital + interest + BSMV + KKDF |
| `ppTotalPaymentCLOSED` | money | Total collected |

---

### qtPaymentPlanCalcDetails
Plan-level financial parameters and contribution/commission details. One row per plan per type.

| Column | Type | Description |
|---|---|---|
| `BaseID` | int | Primary key |
| `pptPaymentPlanID` | int | FK to `qtPaymentPlan.BaseID` |
| `pptPaymentPlanType` | int | Plan type: `0`=Main, `-1`=Balloon, `-6`=Fees, etc. |
| `pptCreditCode` | varchar | Credit type override for this plan segment |
| `pptOpeningFee` | money | File/opening fee amount |
| `pptDealerParticipation` | decimal | Dealer contribution amount/percentage |
| `pptBrandParticipation` | decimal | Brand/distributor contribution |
| `pptCustomerParticipation` | decimal | Customer down-payment contribution |
| `pptDealerCommission` | money | Dealer commission amount |
| `pptTotalEffectiveInterest` | money | Total effective interest (IRR basis) |
| `pptEffectiveInterestRate` | decimal | IRR percentage |
| `pptqtEndUserProductID` | int | FK to `qtEndUserProducts.BaseID` (for vehicle/insurance sub-plans) |

---

### qtPaymentReceipt
Incoming payment receipts. One row per payment received.

| Column | Type | Description |
|---|---|---|
| `BaseID` | int | Primary key |
| `qprQuotationID` | int | FK to `Quotation.BaseID` |
| `qprReceiptRef` | varchar | External receipt reference number |
| `qprReceiptAmount` | money | Total receipt amount |
| `qprReceiptAmountClosed` | money | Amount already allocated to installments |
| `qprCurrencyCode` | varchar | Currency of receipt |
| `qprClientIdentityNr` | varchar | Customer national ID on receipt |
| `qprReceiptDate` | date | Date payment received |

**Derived:** Unallocated/advance balance = `qprReceiptAmount - ISNULL(qprReceiptAmountClosed, 0)`

---

### qtPaymentAlloc
Allocation of a receipt to an installment row (main installments).

| Column | Type | Description |
|---|---|---|
| `BaseID` | int | Primary key |
| `qpaPaymentReceiptID` | int | FK to `qtPaymentReceipt.BaseID` |
| `qpaPaymentPlanDetailID` | int | FK to `qtPaymentPlanDetail.BaseID` |
| `qpaAllocationRef` | varchar | Allocation reference number |
| `qpaAllocationDate` | date | Date allocated |
| `qpaReceiptAmount` | money | Total allocated from receipt |
| `qpaCapitalAmountCLOSED` | money | Principal portion allocated |
| `qpaInterestAmountCLOSED` | money | Interest portion allocated |
| `qpaBSMVCLOSED` | money | BSMV portion allocated |
| `qpaKKDFCLOSED` | money | KKDF portion allocated |
| `qpaIsForgivenAlloc_fl` | bit | `1` = forgiven allocation (waived debt) |
| `MAS_CRDATE` | datetime | Record creation timestamp |
| `qpaUserID` | int | User who created allocation |

---

### qtPaymentAllocDetail
Allocation of a receipt to fee/commission rows (non-installment types).

| Column | Type | Description |
|---|---|---|
| `BaseID` | int | Primary key |
| `MasterID` | int | FK to `qtPaymentAlloc.BaseID` |
| `qpadPaymentPlanDetailID` | int | FK to `qtPaymentPlanDetail.BaseID` (fee/commission row) |
| `qpadCapitalAmountCLOSED` | money | Principal allocated |
| `qpadInterestAmountCLOSED` | money | Interest allocated |
| `qpadBSMVCLOSED` | money | BSMV allocated |
| `qpadKKDFCLOSED` | money | KKDF allocated |
| `qpadCancellationID` | int | FK to cancellation record if reversed (null = active) |
| `MAS_CRDATE` | datetime | Record creation timestamp |

---

### qtPaymentAllocCancellation
Records reversal/cancellation of an allocation.

| Column | Type | Description |
|---|---|---|
| `BaseID` | int | Primary key |
| `qpacAllocBaseID` | int | FK to `qtPaymentAlloc.BaseID` being cancelled |
| `qpacPaymentPlanDetailID` | int | FK to `qtPaymentPlanDetail.BaseID` |
| `qpacAllocationDate` | date | Original allocation date |
| `qpacCancellationDate` | date | Cancellation date |
| `qpacCapitalAmountCLOSED` | money | Principal reversed |
| `qpacInterestAmountCLOSED` | money | Interest reversed |
| `qpacBSMVCLOSED` | money | BSMV reversed |
| `qpacKKDFCLOSED` | money | KKDF reversed |

---

### PaymentVoucherDetail
Bridge between operational data and GL postings. One row per GL posting action.

| Column | Type | Description |
|---|---|---|
| `BaseID` | int | Primary key |
| `QuotationID` | int | FK to `Quotation.BaseID` |
| `SourceID` | int | FK source: `qtPaymentPlanDetail.BaseID` (gltype=8) or `qtPaymentAllocDetail.BaseID` (gltype=12) |
| `GLType` | int | Posting type code. Commonly used: `1`=Opening, `8`=Accrual, `12`=Collection. Full code list is defined in `AccountGeneralLedgerTypes.GLTypeID` |
| `Status` | int | `1`=Normal, `2`=Reverse (cancellation entry), `3`=Error |
| `FisNo` | varchar | Voucher number in accounting |
| `PaymentPlanType` | int | Plan type matching the source row |
| `SourceAmount1` | decimal | Type indicator (e.g. `2` for specific accrual type) |
| `SourceAmount2` | decimal | Principal amount |
| `SourceAmount3` | decimal | Interest amount |
| `SPSource` | varchar | Processing source string; contains `gecodeme=0` (on-time) or `gecodeme=2` (late) |
| `CancelBaseID` | int | FK to original voucher when this row is a reversal |

---

### AccountGeneralLedgerTypes
Canonical lookup for accounting posting/menu codes used by `PaymentVoucherDetail.GLType`.

| Column | Type | Description |
|---|---|---|
| `GLTypeID` | int | Accounting menu id; maps to `PaymentVoucherDetail.GLType` |
| `GLTypeCode` | varchar | Short code (for example `GL1`, `GLP2`, `GL12`) |
| `GLDescription_mlg` | int | Multilingual description key; resolve with `dbo.getDataFromLangId(...,2)` |

---

### h_gl
General ledger entries. Referenced for balance reconciliation.

| Column | Type | Description |
|---|---|---|
| `GANALIZKOD1` | varchar | Credit code (matches `Quotation.qtQuoteCode`) |
| `GFINHESKODU` | varchar | GL account code (e.g. `1200`, `1400`, `1700`) |
| `GTUTARIT` | money | Transaction amount |
| `GTIPI` | char | Sign: `A`=Debit, `B`=Credit |
| `TRN_TARIHI` | date | Transaction date |
| `TRN_MENU` | varchar | Transaction source menu (e.g. `TGLP1`=GL posting) |
| `TRN_EVRAKNO` | varchar | Document/voucher reference |

**GL account filter patterns:**
- Core credit accounts: `GFINHESKODU LIKE '1[2,4,7]%'`
- Extended (includes savings): `GFINHESKODU LIKE '1[2,4,7,5]%'`

---

### Interest
Rotating credit (`ppProductTypeID=19`) accrual header. One row per accrual event.

| Column | Type | Description |
|---|---|---|
| `BaseID` | int | Primary key |
| `inQuotationID` | int | FK to `Quotation.BaseID` |
| `inNetAmount` | money | Net interest amount |
| `inTax` | money | BSMV tax amount |
| `inTax2` | money | KKDF fund amount |
| `inGrossAmount` | money | Total (net + taxes) |
| `inStartDate` | date | Accrual period start |
| `inEndDate` | date | Accrual period end |
| `inAccrualDate` | date | Date posted to GL (null = not yet posted) |

---

### InterestDetail
Daily accrual breakdown under `Interest`.

| Column | Type | Description |
|---|---|---|
| `BaseID` | int | Primary key |
| `MasterID` | int | FK to `Interest.BaseID` |
| `indAmount` | money | Daily interest amount |
| `indNbofDays` | int | Number of days in this tranche |
| `indStartDate` | date | Start date of tranche |

---

### qtEndUserProducts
Products financed under a credit (vehicles, insurance, etc.).

| Column | Type | Description |
|---|---|---|
| `BaseID` | int | Primary key |
| `qteupQuotationID` | int | FK to `Quotation.BaseID` |
| `qteupEndUserProductID` | int | FK to `EndUserProduct.BaseID` (level 1) |
| `qteupBrand` | int | FK to `vehBrand.BaseID` |
| `qteupFamily` | int | FK to `vehFamily.BaseID` |
| `qteupFamilyDetailID` | int | FK to `vehFamilyDetail.BaseID` |
| `qteupVehicleVINID` | int | FK to `VehicleVIN.BaseID` |
| `qteupQuantity` | int | Quantity |
| `qteupUnitPrice` | money | Unit price |
| `qteupAmount` | money | Total product amount |
| `qteupExchangeAmount` | money | Product invoice/exchange amount (used to derive product down payment) |
| `qteupPeriod` | int | Term/period of this product's payment plan (number of installments) |
| `qteupCurrencyCode` | varchar | Currency of product |
| `qteupModelYear` | int | Model year |
| `qteupIsSecondHand_fl` | bit | `1`=Second-hand vehicle |
| `qteupIsSideProduct_fl` | bit | `1`=Side-product row (service/add-on), `0`=main product |
| `qteupQuotationServicesID` | int | FK to `quotationServices.BaseID` for side-product linkage |

---

### EndUserProduct
Product definition hierarchy (3 levels: Main → Sub → Detail).

| Column | Type | Description |
|---|---|---|
| `BaseID` | int | Primary key |
| `eupName_mlg` | int | FK to multilingual name |
| `eupUpLevelID` | int | FK to parent `EndUserProduct.BaseID` (null at top level) |

**Usage:** Self-join twice to resolve Main → Sub → Detail product hierarchy.

```
-- Resolve 3-level product hierarchy
left join EndUserProduct eup  on eup.BaseID  = qeup.qteupEndUserProductID  -- Detail (level 1)
left join EndUserProduct eup2 on eup2.BaseID = eup.eupUpLevelID             -- Sub (level 2)
left join EndUserProduct eup3 on eup3.BaseID = eup2.eupUpLevelID            -- Main (level 3)

DetailProduct = dbo.getdatafromlangID(eup.eupName_mlg,  2)
SubProduct    = dbo.getdatafromlangID(eup2.eupName_mlg, 2)
MainProduct   = dbo.getdatafromlangID(eup3.eupName_mlg, 2)
```

---

### vehBrand
Vehicle brand definitions.

| Column | Type | Description |
|---|---|---|
| `BaseID` | int | Primary key |
| `vehBrandName_mlg` | int | FK to multilingual brand name |

---

### vehFamily
Vehicle family / model type definitions.

| Column | Type | Description |
|---|---|---|
| `BaseID` | int | Primary key |
| `vehFamilyName_mlg` | int | FK to multilingual family name |
| `vehRegTypeID` | int | FK to `vehRegType.BaseID` (vehicle registration type) |

---

### vehFamilyDetail
Vehicle model detail / variant definitions.

| Column | Type | Description |
|---|---|---|
| `BaseID` | int | Primary key |
| `fmdFamilyDetailName_mlg` | int | FK to multilingual variant name |

---

### vehModelYear
Vehicle model year definitions.

| Column | Type | Description |
|---|---|---|
| `BaseID` | int | Primary key |

---

### quotationServices
Side-product / service linkage on a credit product row.

| Column | Type | Description |
|---|---|---|
| `BaseID` | int | Primary key — referenced by `qtEndUserProducts.qteupQuotationServicesID` |
| `ServiceID` | int | FK to `service.BaseID` |

---

### service
Service / side-product definition (insurance, warranty, etc.).

| Column | Type | Description |
|---|---|---|
| `BaseID` | int | Primary key |
| `serviceName_mlg` | int | FK to multilingual service name |
| `srvProvider` | int | FK to `srvprovider.BaseID` |

---

### srvprovider
Service provider definition.

| Column | Type | Description |
|---|---|---|
| `BaseID` | int | Primary key |
| `srvProName` | varchar | Provider name |
Individual vehicle record (chassis-level).

| Column | Type | Description |
|---|---|---|
| `BaseID` | int | Primary key |
| `VehicleID` | int | FK to `Vehicle.BaseID` |
| `VIN` | varchar | Chassis number |
| `EngineVIN` | varchar | Engine number |
| `LicensePlate` | varchar | License plate |
| `EGMReferenceNo` | varchar | EGM registration reference |
| `vehEGMStatus` | int | EGM pledge status: `IN(4,3)`=removed/inactive, other=active |

---

### VehicleVINEGM
Pledge event history per vehicle. Each pledge add or removal is a row.

| Column | Type | Description |
|---|---|---|
| `BaseID` | int | Primary key |
| `egmVehicleVINID` | int | FK to `VehicleVIN.BaseID` |
| `egmRequestType` | int | Event type: `IN(1,101,16,5)`=pledge added, `IN(102,13,2,22,29)`=pledge removed |
| `egmRequestDate` | datetime | Event date |
| `egmRequestUser` | int | FK to `Q_USERS.BaseID` |

---

### VehicleVINEGM_Deprivation
First-lien / rights-holder history.

| Column | Type | Description |
|---|---|---|
| `Id` | int | Primary key |
| `vedVehicleVINID` | int | FK to `VehicleVIN.BaseID` |
| `vedUnitName` | varchar | Name of rights holder |
| `vedRequestDate` | datetime | Date of deprivation record |

---

### qtSecurity
Links a credit to its collateral definitions.

| Column | Type | Description |
|---|---|---|
| `BaseID` | int | Primary key |
| `qtsQuotationID` | int | FK to `Quotation.BaseID` |
| `qtsCustSecurityID` | int | FK to `CustomerSecurityDefinitions.BaseID` |
| `qtsIsReceived` | bit | `1`=collateral physically received |
| `qtsValue` | money | Value of collateral |
| `qtQuoteRelatedProduct_fl` | bit | `1`=main product collateral, `0`=additional collateral |

---

### CustomerSecurityDefinitions
Collateral definitions at the customer level.

| Column | Type | Description |
|---|---|---|
| `BaseID` | int | Primary key |
| `csdCustomerBaseID` | int | FK to `Customer.BaseID` |
| `csdQuoteCode` | varchar | Credit code this definition belongs to |
| `csdSecurityName` | varchar | Description of collateral |
| `csdSecurityTypeCode` | varchar | `03`=Vehicle pledge, `09`=Guarantor |
| `csdSecurityAmount` | money | Declared security value |
| `csdSecurityAmountCurrency` | varchar | Currency |
| `csdExpertiseAmount` | money | Appraisal/expertise value |
| `csdIsActive` | bit | `1`=Active collateral |
| `csdVehicleVINID` | int | FK to `VehicleVIN.BaseID` if vehicle-based |
| `csdProposalRoleID` | int | FK to `ProposalRole.BaseID` if guarantor-based |

---

### ProposalRole
Credit application roles (applicants, guarantors, co-borrowers).

| Column | Type | Description |
|---|---|---|
| `BaseID` | int | Primary key |
| `prProposalID` | int | FK to `Proposal.BaseID` |
| `prCustomerRoleID` | int | Role: `2`=Guarantor (Kefil) |
| `prRoleIsPerson` | bit | `1`=Individual person, `0`=Company |
| `prPersonID` | int | FK to `Person.BaseID` |
| `prCompanyID` | int | FK to `company.BaseID` |

---

### csServiceRefundRights
Cancellation / refund rights records. Used in reescompte to identify cancelled credits.

| Column | Type | Description |
|---|---|---|
| `BaseID` | int | Primary key |
| `csrefundQuotationID` | int | FK to `Quotation.BaseID` |
| `csrefundCallStatusID` | int | Status: `3` or `5`=Cancelled |
| `csrefundRequestDate` | date | Date cancellation was requested |

---

### Dealer
Dealer definitions.

| Column | Type | Description |
|---|---|---|
| `BaseID` | int | Primary key |
| `dealName` | varchar | Dealer name |

---

### CFS_SystemParams
System configuration parameters.

| Column | Type | Description |
|---|---|---|
| `ParamKey` | varchar | Parameter name (e.g. `ILLIQUID_CLAIM`) |
| `ParamValue` | varchar | Parameter value |

---

## Code / Value Dictionaries

### CreditStatus.csCreditCode
| Code | Stage | Meaning | In portfolio? |
|---|---|---|---|
| `VA` | 1 | Vadeli Aktif — Active, contract signed / credit activated | Open |
| `LI` | 2 | Liquidation (Likidasyon) — Credit funds disbursed / transferred to the dealer | Open |
| `GD` | 3 | Definitive Management — All operational checks complete (documents, collateral) | Open |
| `END` | 4 | Closed — Fully paid or terminated | Closed |

**Lifecycle ordering:** `VA → LI → GD → END` (sequential progression; all three of `VA`/`LI`/`GD` are part of the live, performing portfolio).

**Standard open filter:** `csCreditCode IN ('VA','LI','GD')`
**Including recently closed:** `csCreditCode IN ('VA','LI','GD') OR (csCreditCode='END' AND csQuotationStatusLastUpdate >= @cutoffDate)`
**Stage filters:**
- Approved-but-not-yet-funded only: `csCreditCode = 'VA'`
- Funded but pending operational controls: `csCreditCode = 'LI'`
- Fully validated and live: `csCreditCode = 'GD'`

### qtPaymentPlanDetail.pPaymentPlanType and qtPaymentPlanCalcDetails.pptPaymentPlanType
Shared type code used in both `qtPaymentPlanDetail.pPaymentPlanType` and `qtPaymentPlanCalcDetails.pptPaymentPlanType`.

| Code | Meaning |
|---|---|
| `> 0` (positive int) | **EndUserProduct sub-plan** — one positive integer per product in a multi-product credit (e.g. 1, 2, 3…). Each positive type represents one `EndUserProduct` in the credit. |
| `0` | **Aggregate plan** — sum of all product sub-plans (`> 0` rows). This is the consolidated view across all products in the credit. |
| `-1` | **Single-product fallback** — used instead of a positive type when a credit has only one product. Functionally equivalent to a `> 0` sub-plan. |
| `-4` | Accrual Interest (Tahakkuk Faiz) |
| `-5` | Late Interest (Temerrüt) — generated when an installment becomes overdue |
| `-6` | Fees (Masraf) — file/processing fee |
| `-7` | Tahakkuk Kur Farkı — Accrual Exchange Rate Difference |
| `-8` | Ödeme Kur Farkı — Payment Exchange Rate Difference |
| `-10` | Commission (Komisyon) |

**Type relationships:**
- `pPaymentPlanType > 0` rows = individual product sub-plans; each links to one `EndUserProduct` via `qtPaymentPlanCalcDetails.pptQtEndUserProductID`.
- `pPaymentPlanType = 0` row = the aggregate; its financial columns equal the sum of all `> 0` rows.
- `pPaymentPlanType = -1` = single-product plan; sometimes used instead of a positive type when there is only one product.
- When querying the main installment schedule use `pPaymentPlanType = 0`.
- When querying per-product installment detail use `pPaymentPlanType > 0 OR pPaymentPlanType = -1`.

**All billable types:** `pPaymentPlanType IN (0, -1, -4, -5, -6, -7, -8, -10)` plus `pPaymentPlanType > 0`

### qtPaymentPlanDetail.ppdRowCreateTypeCode
| Code | Meaning |
|---|---|
| `LT` | Created during follow-up/cut (Takip) |
| `ES` | Created during early closure (Erken Satış) |

### qtPaymentPlan.ppProductTypeID
| Code | Meaning |
|---|---|
| `1` | FIXED — Equal installments (Eşit Taksitli) |
| `2` | BALLOON — Balloon payment (Balon Ödemeli) |
| `3` | MULTISTEP — Multi-step / stepped payment plan |
| `10` | SPOT — Spot rate |
| `19` | ROTATIF — Rotating credit, daily interest accrual |
| `37` | GRACE — Grace period plan |
| `38` | PERIODIC — Periodic payment plan |

### Quotation.qtCreditTypeCode
| Code | Meaning |
|---|---|
| `AK` | Auto Loan (Otomobil Kredisi) |
| `KK` | Mortgage (Konut Kredisi) |
| `FK` | Consumer Loan (Tüketici Kredisi) |
| `IF` | (Finance) |
| `DG` | (Guarantee-type) |

### Quotation.qtCustTypeOfUseID
| Code | Meaning |
|---|---|
| `1` | Personal (Bireysel) |
| `2` | Commercial / Legal entity (Tüzel) |

### PaymentVoucherDetail.GLType
| Code | Meaning |
|---|---|
| `1` | `GL1` - Credit opening posting |
| `3` | `GL3` - Credit transfer |
| `4` | `GLP1` - Credit interest reescompte |
| `5` | `GLP1A` - Credit interest reescompte variant |
| `8` | `GLP2` - Installment moved to delay/late |
| `11` | `GL11` - Miscellaneous collection |
| `12` | `GL12` - Collection matching/allocation |
| `13` | `GLP3` - Daily spreading posting |
| `14` | `GL1C` - Credit opening cancellation |
| `16` | `GL6` - Fee accounting |
| `19` | `GL14` - Credit closure accounting |
| `21` | `GLP11` - General provisions |
| `22` | `GLP5` - Monthly spreading accounting |
| `23` | `GL1CT` - Revocable commitments accounting |
| `24` | `GL11V` - Collection transfer between accounts |
| `25` | `GL11VD` - FX transfer between collection accounts |
| `27` | `GL9` - Collateral accounting |
| `28` | `GLP12` - Specific provisions |
| `29` | `GL17` - Credit update |
| `31` | `GLP6` - Exit from follow-up account |
| `32` | `GL18` - Discount change |
| `33` | `GL33` - Service-side collection matching |
| `34` | `GL34` - Principal update |
| `35` | `GL35` - Contribution collection |
| `36` | `GL36` - Transfer from temporary account to bank account |
| `37` | `GL37` - Variable-rate accrual |
| `53` | `GL53` - Credit closure sweep |

**Note:** Keep this dictionary synced from `AccountGeneralLedgerTypes` and not from hardcoded assumptions.

### PaymentVoucherDetail.Status
| Code | Meaning |
|---|---|
| `1` | Normal — standard posting |
| `2` | Reverse — cancellation/reversing entry; pairs with original via `CancelBaseID` |
| `3` | Error — failed posting, exclude from financial sums |

**Sign convention when reconciling**: when `Status = 2` rows reference an
original `Status = 1` row via `CancelBaseID`, the reversal nets the original
amount. Typical pattern:

```sql
CASE
    WHEN p.Status = 1
      OR (p.Status = 2 AND ISNULL(pl.Status, 0) = 2) THEN  p.SourceAmount2
    WHEN p.Status = 2 AND ISNULL(pl.Status, 0) = 1   THEN -p.SourceAmount2
END
```

Always exclude `Status = 3` (error) from financial aggregates.

### PaymentVoucherDetail.SPSource
| Pattern | Meaning |
|---|---|
| `%gecodeme=0%` | On-time payment |
| `%gecodeme=2%` | Late payment |

### h_gl.GTIPI
| Code | Meaning |
|---|---|
| `A` | Debit (adds to balance) |
| `B` | Credit (subtracts from balance) |

### CustomerSecurityDefinitions.csdSecurityTypeCode
| Code | Meaning |
|---|---|
| `03` | Vehicle pledge (Araç Rehni) |
| `09` | Guarantor (Kefil) |

---

## Key Financial Formulas

### Remaining balance due on an installment
```
KalanBorc = ppTotalPayment
           - ppTotalPaymentCLOSED
           - ppInterestAmountDISCOUNT
           - ppInterestAmountDISCOUNTCLOSED
           - ppBSMVDISCOUNT
           - ppBSMVDISCOUNTCLOSED
           - ppKKDFDISCOUNT
           - ppKKDFDISCOUNTCLOSED
```

### Accrued but unpaid interest (open receivable)
```
TahakkukFaiz = ppInterestAmount - ppInterestAmountCLOSED - ppInterestAmountDISCOUNTCLOSED
TahakkukBSMV = ppBMSV         - ppBSMVCLOSED           - ppBSMVDISCOUNTCLOSED
TahakkukKKDF = ppKKDF         - ppKKDFCLOSED           - ppKKDFDISCOUNTCLOSED
```
(Apply only where `ppAssumedDueDate < GETDATE()`)

### Reescompte (pro-rated current installment interest)
```
ppStart = prior installment ppAssumedDueDate  (or ppActivationDate for installment #1)
ppEnd   = current installment ppAssumedDueDate

ReeskontTutari = ROUND(
    (ppInterestAmount / DATEDIFF(DD, ppStart, ppEnd))
    * (DATEDIFF(DD, ppStart, @reportDate) + 1),
    2)
```
**Edge cases:**
- Cut credit (`ppCutDate IS NOT NULL`): use GL posted amount from `h_gl` with 30-day fixed period
- Cancelled credit: same as cut
- Rotating (`ppProductTypeID = 19`): use `SUM(InterestDetail.indAmount)` where `indStartDate <= @reportDate` and `Interest.inAccrualDate IS NULL`
- If full period elapsed: return `ppInterestAmount`
- If `DayTot = 0`: return `0`

### Outstanding principal
```
KalanAnapara = ppCapitalAmount - ppCapitalAmountCLOSED
```
(Alternatively read `ppRemainingCapitalAmount` — but verify against above for accuracy)

### Advance (unallocated receipt) balance
```
AvansBalance = qprReceiptAmount - ISNULL(qprReceiptAmountClosed, 0)
```

### GL balance for a credit
```
Bakiye = SUM(CASE GTIPI WHEN 'A' THEN GTUTARIT ELSE -GTUTARIT END)
WHERE GANALIZKOD1 = @creditCode
  AND GFINHESKODU LIKE '1[2,4,7]%'
```

### Late day count
```
LateDayCount = DATEDIFF(DAY, MIN(ppAssumedDueDate of overdue open installments), GETDATE())
```
(Use `dbo.get_LateDayCountWithBalance(QuotationID, @date)` directly when available)

---

## Installment Period Derivation (for Reescompte)
To get `ppStart` for installment N:
- If N=1: `ppStart = ISNULL(ppActivationDate, csCreditOpeningLastUpdate)`
- If N>1: `ppStart = ppAssumedDueDate` of installment N-1 (same `pPaymentPlanID`, `ppPaymentAmountRowIndex = N-1`, same `pPaymentPlanType`)

---

## Query Blueprints

### 1. Credit + Plan + Installment Base Join
```
qtPaymentPlanDetail pd
  JOIN qtPaymentPlan p            ON p.BaseID = pd.pPaymentPlanID
  JOIN Quotation q                ON q.BaseID = p.ppQuotationID
  JOIN CreditStatus c             ON c.csQuotationID = q.BaseID
WHERE c.csCreditCode IN ('VA','LI','GD')
  AND pd.pPaymentPlanType = 0     -- type 0 = aggregate EndUserProductPlan (main installments)
```

### 2. Portfolio Count/Amount Snapshot (grouped by quarter and customer type)
```
SELECT
  Tur           = CASE qtCustTypeOfUseID WHEN 1 THEN 'Personal' ELSE 'Commercial' END,
  Yil           = DATEPART(YYYY, qtQuotationDate),
  Ceyrek        = DATEPART(QUARTER, qtQuotationDate),
  KrediAdet     = SUM(CASE WHEN csCreditCode IN ('VA','LI','GD') THEN 1 ELSE 0 END),
  KrediTutar    = SUM(CASE WHEN csCreditCode IN ('VA','LI','GD') THEN qtAmttoFinance ELSE 0 END),
  KapaliAdet    = SUM(CASE WHEN csCreditCode = 'END' THEN 1 ELSE 0 END)
FROM Quotation q, CreditStatus c
WHERE q.BaseID = c.csQuotationID
GROUP BY DATEPART(YYYY,qtQuotationDate), DATEPART(QUARTER,qtQuotationDate), qtCustTypeOfUseID
```

### 3. Installment Detail with All Financial Components
```
SELECT
  CreditNumber          = q.qtQuoteCode,
  CreditID              = q.BaseID,
  InstallmentNo         = pd.ppPaymentAmountRowIndex,
  DueDate               = pd.ppAssumedDueDate,
  PaymentRef            = pd.ppPaymentRef,
  IsClosed              = pd.ppInstallmentIsCLOSED_FL,
  ClosedDate            = pd.ppInstallmentClosedDate,
  TotalDue              = pd.ppTotalPayment,
  TotalPaid             = pd.ppTotalPaymentCLOSED,
  RemainingDue          = pd.ppTotalPayment - pd.ppTotalPaymentCLOSED
                          - pd.ppInterestAmountDISCOUNT - pd.ppInterestAmountDISCOUNTCLOSED
                          - pd.ppBSMVDISCOUNT - pd.ppBSMVDISCOUNTCLOSED
                          - pd.ppKKDFDISCOUNT - pd.ppKKDFDISCOUNTCLOSED,
  Capital               = pd.ppCapitalAmount,
  CapitalPaid           = pd.ppCapitalAmountCLOSED,
  CapitalRemaining      = pd.ppRemainingCapitalAmount,
  Interest              = pd.ppInterestAmount,
  InterestPaid          = pd.ppInterestAmountCLOSED,
  InterestDiscountCalc  = pd.ppInterestAmountDISCOUNT,
  InterestDiscountAppl  = pd.ppInterestAmountDISCOUNTCLOSED,
  InterestToday         = pd.ppInterestAmountTODAY,
  BSMV                  = pd.ppBMSV,
  BSMVPaid              = pd.ppBSMVCLOSED,
  BSMVDiscountCalc      = pd.ppBSMVDISCOUNT,
  BSMVDiscountAppl      = pd.ppBSMVDISCOUNTCLOSED,
  KKDF                  = pd.ppKKDF,
  KKDFPaid              = pd.ppKKDFCLOSED,
  AccrualDate           = pd.ppAccuralDate,
  RowType               = pd.ppdRowCreateTypeCode,
  ComponentType         = pd.pPaymentPlanType
FROM qtPaymentPlanDetail pd (NOLOCK)
JOIN qtPaymentPlan p (NOLOCK)   ON p.BaseID = pd.pPaymentPlanID
JOIN Quotation q (NOLOCK)       ON q.BaseID = p.ppQuotationID
JOIN CreditStatus c (NOLOCK)    ON c.csQuotationID = q.BaseID
WHERE c.csCreditCode IN ('VA','LI','GD')
ORDER BY q.BaseID, pd.ppAssumedDueDate
```

### 4. Credit-Level Overdue Portfolio
```
SELECT
  q.qtClientNationalID,
  q.qtClientName,
  q.qtQuoteCode,
  GGS              = lb.LateDayCount,
  GecikmeBakiyesi  = lb.LateBalance,
  AvansBalance     = SUM(pr.qprReceiptAmount - ISNULL(pr.qprReceiptAmountClosed, 0)),
  Temerrut         = SUM(CASE WHEN pd.pPaymentPlanType = -5 THEN pd.ppTotalPayment ELSE 0 END),
  TemerrutOdenen   = SUM(CASE WHEN pd.pPaymentPlanType = -5 THEN pd.ppTotalPaymentCLOSED ELSE 0 END),
  VadesiGelenAnapara = SUM(CASE WHEN pd.pPaymentPlanType=0
                               AND CAST(pd.ppAssumedDueDate AS DATE) <= CAST(GETDATE() AS DATE)
                               THEN pd.ppCapitalAmount ELSE 0 END),
  ToplamAnapara    = SUM(CASE WHEN pd.pPaymentPlanType=0 THEN pd.ppCapitalAmount ELSE 0 END)
FROM Quotation q (NOLOCK)
JOIN CreditStatus c (NOLOCK)       ON c.csQuotationID = q.BaseID AND c.csCreditCode IN ('VA','LI','GD')
JOIN qtPaymentPlan p (NOLOCK)      ON p.ppQuotationID = q.BaseID
OUTER APPLY dbo.get_LateDayCountWithBalance(q.BaseID, NULL) lb
OUTER APPLY (SELECT qprReceiptAmount, qprReceiptAmountClosed
             FROM qtPaymentReceipt pr (NOLOCK)
             WHERE pr.qprClientIdentityNr = q.qtClientNationalID) pr
OUTER APPLY (SELECT * FROM qtPaymentPlanDetail pd (NOLOCK)
             WHERE pd.pPaymentPlanID = p.BaseID
  AND pd.pPaymentPlanType IN (0,-4,-5,-6)) pd  -- 0=aggregate, -4=accrual interest, -5=late interest, -6=fees
GROUP BY q.qtClientNationalID, q.qtClientName, q.qtQuoteCode, lb.LateDayCount, lb.LateBalance
```

### 5. Allocation Reconciliation (Receipt vs Installment)
```
SELECT
  a.qpaPaymentReceiptID   AS ReceiptID,
  a.qpaPaymentPlanDetailID AS PlanDetailID,
  a.qpaAllocationRef,
  a.qpaAllocationDate,
  a.qpaCapitalAmountCLOSED,
  a.qpaInterestAmountCLOSED,
  a.qpaBSMVCLOSED,
  a.qpaKKDFCLOSED
FROM qtPaymentAlloc a (NOLOCK)
-- Exclude cancelled: join qtPaymentAllocCancellation and filter BaseID IS NULL
WHERE NOT EXISTS (SELECT 1 FROM qtPaymentAllocCancellation ac WHERE ac.qpacAllocBaseID = a.BaseID)

UNION ALL

SELECT
  a.qpaPaymentReceiptID,
  ad.qpadPaymentPlanDetailID,
  a.qpaAllocationRef,
  a.qpaAllocationDate,
  ad.qpadCapitalAmountCLOSED,
  ad.qpadInterestAmountCLOSED,
  ad.qpadBSMVCLOSED,
  ad.qpadKKDFCLOSED
FROM qtPaymentAllocDetail ad (NOLOCK)
JOIN qtPaymentAlloc a (NOLOCK) ON a.BaseID = ad.MasterID
WHERE ad.qpadCancellationID IS NULL
```

### 6. GL Missing Accrual Detection
```
SELECT pd.BaseID, pd.ppAssumedDueDate, pd.ppAccuralDate, pv.BaseID AS VoucherID
FROM qtPaymentPlanDetail pd (NOLOCK)
JOIN qtPaymentPlan p (NOLOCK)     ON p.BaseID = pd.pPaymentPlanID
JOIN CreditStatus c (NOLOCK)      ON c.csQuotationID = p.ppQuotationID
                                 AND c.csCreditCode IN ('VA','LI','GD')
LEFT JOIN PaymentVoucherDetail pv (NOLOCK)
       ON pv.SourceID = pd.BaseID AND pv.GLType = 8 AND pv.SourceAmount1 = 2
WHERE (pd.pPaymentPlanType = -1 OR pd.pPaymentPlanType > 0)  -- single-product or aggregate plans
  AND CAST(pd.ppAssumedDueDate AS DATE) < CAST(GETDATE() AS DATE)
  AND pv.BaseID IS NULL
  AND pd.ppInterestAmount > 0
```

### 7. Follow-up / Cut Credits
```
SELECT
  q.qtQuoteCode,
  p.ppCutDate,
  p.ppCutDPD,
  p.ppCutMinAssumedDueDate,
  p.ppCutCapitalAmount,
  lb.LateDayCount,
  lb.LateBalance
FROM qtPaymentPlan p (NOLOCK)
JOIN Quotation q (NOLOCK)       ON q.BaseID = p.ppQuotationID
JOIN CreditStatus c (NOLOCK)    ON c.csQuotationID = q.BaseID AND c.csCreditCode IN ('VA','LI','GD')
OUTER APPLY dbo.get_LateDayCountWithBalance(q.BaseID, NULL) lb
WHERE p.ppCutDate IS NOT NULL
```

### 8. Collateral Report (All Security Types)
```
SELECT
  q.qtQuoteCode,
  SecurityType   = t.TeminatTuru,         -- AracRehni / Kefil / EkTeminat
  SecurityTypeCode = t.SecurityTypeCode,  -- 03 / 09
  Description    = t.TeminatAciklamasi,
  Amount         = t.TeminatTutari,
  IsActive       = t.Aktif,
  ExpertiseAmount = t.ExpertizTutari,
  AddPledgeDate  = veAddPledge.egmRequestDate,
  RemovePledgeDate = veRemovePledge.egmRequestDate
FROM Quotation q (NOLOCK)
JOIN Proposal prop (NOLOCK)     ON prop.propQuotationID = q.BaseID
JOIN qtPaymentPlan pp (NOLOCK)  ON pp.ppQuotationID = q.BaseID
-- CROSS APPLY with UNION of three source types (see Müşteri Teminatları.sql pattern)
```

### 9. Rotating Credit Interest Summary
```
SELECT
  q.qtQuoteCode,
  TotalInterest  = SUM(i.inNetAmount),
  TotalBSMV      = SUM(i.inTax),
  TotalKKDF      = SUM(i.inTax2),
  TotalGross     = SUM(i.inGrossAmount),
  StartDate      = MIN(i.inStartDate),
  EndDate        = MAX(i.inEndDate)
FROM qtPaymentPlan p (NOLOCK)
JOIN Quotation q (NOLOCK)       ON q.BaseID = p.ppQuotationID
JOIN CreditStatus c (NOLOCK)    ON c.csQuotationID = q.BaseID AND c.csCreditCode IN ('VA','LI','GD')
LEFT JOIN Interest i (NOLOCK)   ON i.inQuotationID = q.BaseID
WHERE p.ppProductTypeID = 19
GROUP BY q.qtQuoteCode
```

### 10. Per-Product Payment Plan Detail
Used when a credit finances multiple products and each product has its own sub-plan (`pPaymentPlanType > 0` or `-1`).

```sql
SELECT
  ProductID                = e.BaseID,
  AmountOfProductPaymentPlan = e.qteupAmount,
  PeriodOfProductPaymentPlan = e.qteupPeriod,
  InstallmentNumber        = pd.ppPaymentAmountRowIndex,
  InstallmentDate          = pd.ppAssumedDueDate,
  InstallmentNextWorkingDate = (SELECT TOP 1 MinTarih FROM dbo.getWorkingDate_tbl_inline(pd.ppAssumedDueDate)),
  InterestAmount           = pd.ppInterestAmount,
  InterestAmountDiscount   = pd.ppInterestAmountDISCOUNT + pd.ppInterestAmountDISCOUNTCLOSED,
  InterestAmountClosed     = pd.ppInterestAmountCLOSED,
  CapitalAmount            = pd.ppCapitalAmount,
  CapitalAmountClosed      = pd.ppCapitalAmountCLOSED,
  BSMVTax                  = pd.ppBMSV,
  BSMVTaxClosed            = pd.ppBSMVCLOSED,
  BSMVTaxDiscount          = pd.ppBSMVDISCOUNT + pd.ppBSMVDISCOUNTCLOSED,
  KKDFTax                  = pd.ppKKDF,
  KKDFTaxClosed            = pd.ppKKDFCLOSED,
  KKDFTaxDiscount          = pd.ppKKDFDISCOUNTCLOSED + pd.ppKKDFDISCOUNT,
  InstallmentAccrualDate   = pd.ppAccuralDate
FROM qtPaymentPlanDetail pd (NOLOCK)
JOIN qtPaymentPlanCalcDetails pc (NOLOCK) ON pc.pptPaymentPlanID = pd.pPaymentPlanID
                                          AND pc.pptPaymentPlanType = pd.pPaymentPlanType
JOIN qtEndUserProducts e (NOLOCK)         ON e.BaseID = pc.pptQtEndUserProductID
JOIN qtPaymentPlan p (NOLOCK)             ON p.BaseID = pd.pPaymentPlanID
JOIN Quotation q (NOLOCK)                 ON q.BaseID = p.ppQuotationID
WHERE pd.pPaymentPlanType > 0             -- individual product sub-plans
   OR pd.pPaymentPlanType = -1            -- single-product fallback
ORDER BY e.BaseID, pd.ppAssumedDueDate
```

### 11. Credit Header with Rates and Fees
```sql
SELECT
  CreditNumber      = q.qtQuoteCode,
  CreditID          = q.BaseID,
  ClientIdentity    = q.qtClientNationalID,
  CreditStatus      = CASE WHEN c.csCreditCode IN ('VA','LI','GD') THEN 'Open' ELSE 'Closed' END,
  CreditOpeningDate = c.csCreditOpeningLastUpdate,
  CreditCloseDate   = CASE WHEN c.csCreditCode = 'END' THEN c.csQuotationStatusLastUpdate ELSE NULL END,
  CustomerType      = CASE q.qtCustTypeOfUseID WHEN 1 THEN 'Personal' ELSE 'Commercial' END,
  CreditType        = CASE q.qtCreditTypeCode
                        WHEN 'AK' THEN 'Autoloan'
                        WHEN 'KK' THEN 'Mortgage'
                        WHEN 'FK' THEN 'ConsumerLoan'
                        ELSE 'Other' END,
  BaseRate          = q.ppMonthlyInt,
  CampaignRate      = q.ppMonthlyIntCalc * 100,
  KKDFTaxRate       = q.qtKKDF,
  BSMVTaxRate       = q.qtBMSV,
  OpeningFee        = pc.pptOpeningFee,
  DealerParticipation = pc.pptDealerParticipation,
  BrandParticipation  = pc.pptBrandParticipation,
  CustomerParticipation = pc.pptCustomerParticipation,
  DealerID          = q.qtDealerID,
  DealerName        = d.dealName
FROM qtPaymentPlan p (NOLOCK)
JOIN qtPaymentPlanCalcDetails pc (NOLOCK) ON pc.pptPaymentPlanID = p.BaseID AND pc.pptPaymentPlanType = 0
JOIN Quotation q (NOLOCK)                 ON q.BaseID = p.ppQuotationID
JOIN CreditStatus c (NOLOCK)              ON c.csQuotationID = q.BaseID
                                         AND c.csCreditCode IN ('VA','LI','GD','END')
LEFT JOIN Dealer d (NOLOCK)               ON d.BaseID = q.qtDealerID
```

### 12. DataStructure-Aligned Installment Extract (Operational)
Use this exact filter set when the report must include all billable installment components from `datastructure.sql`.

```sql
SELECT
  CreditNumber = q.qtQuoteCode,
  CreditID = q.BaseID,
  PlanID = p.BaseID,
  PlanDetailID = pd.BaseID,
  ClientIdentity = q.qtClientNationalID,
  InstallmentNumber = pd.ppPaymentAmountRowIndex,
  InstallmentDate = pd.ppAssumedDueDate,
  InstallmentNextWorkingDate = (SELECT TOP 1 MinTarih FROM dbo.getWorkingDate_tbl_inline(pd.ppAssumedDueDate)),
  InterestAmount = pd.ppInterestAmount,
  InterestAmountDiscount = pd.ppInterestAmountDISCOUNT + pd.ppInterestAmountDISCOUNTCLOSED,
  InterestAmountClosed = pd.ppInterestAmountCLOSED,
  CapitalAmount = pd.ppCapitalAmount,
  CapitalAmountClosed = pd.ppCapitalAmountCLOSED,
  BSMVTax = pd.ppBMSV,
  BSMVTaxClosed = pd.ppBSMVCLOSED,
  BSMVTaxDiscount = pd.ppBSMVDISCOUNT + pd.ppBSMVDISCOUNTCLOSED,
  KKDFTax = pd.ppKKDF,
  KKDFTaxClosed = pd.ppKKDFCLOSED,
  KKDFTaxDiscount = pd.ppKKDFDISCOUNTCLOSED + pd.ppKKDFDISCOUNT,
  InstallmentAccrualDate = pd.ppAccuralDate
FROM qtPaymentPlanDetail pd (NOLOCK)
JOIN qtPaymentPlan p (NOLOCK) ON p.BaseID = pd.pPaymentPlanID
JOIN qtPaymentPlanCalcDetails pc (NOLOCK) ON pc.pptPaymentPlanID = p.BaseID AND pd.pPaymentPlanType = 0
JOIN Quotation q (NOLOCK) ON q.BaseID = p.ppQuotationID
JOIN CreditStatus c (NOLOCK) ON c.csQuotationID = q.BaseID AND c.csCreditCode IN ('VA','LI','GD','END')
WHERE pd.pPaymentPlanType IN (0, -5, -6, -10, -7, -8)
ORDER BY q.BaseID, pd.ppAssumedDueDate;
```

### 13. DataStructure-Aligned Receipt Detail
```sql
SELECT
  ReceiptID = pr.BaseID,
  ReceiptReferenceNumber = pr.qprReceiptRef,
  ReceiptAmount = pr.qprReceiptAmount,
  ReceiptAmountClosed = pr.qprReceiptAmountClosed,
  CurrencyCode = pr.qprCurrencyCode,
  ClientIdentity = pr.qprClientIdentityNr,
  CreditCode = pr.qprQuotationID,
  ReceiptDate = pr.qprReceiptDate
FROM qtPaymentReceipt pr (NOLOCK);
```

### 14. DataStructure-Aligned Allocation Detail (Main + Fee/Commission)
```sql
SELECT
  AllocationID = a.BaseID,
  ReceiptID = a.qpaPaymentReceiptID,
  PlanDetailID = a.qpaPaymentPlanDetailID,
  AllocationReferenceNumber = a.qpaAllocationRef,
  AllocationDate = a.qpaAllocationDate,
  AllocatedCapitalAmount = a.qpaCapitalAmountCLOSED,
  AllocatedInterestAmount = a.qpaInterestAmountCLOSED,
  AllocatedBSMVAmount = a.qpaBSMVCLOSED,
  AllocatedKKDFAmount = a.qpaKKDFCLOSED
FROM qtPaymentAlloc a (NOLOCK)
UNION ALL
SELECT
  AllocationID = a.BaseID,
  ReceiptID = a.qpaPaymentReceiptID,
  PlanDetailID = ad.qpadPaymentPlanDetailID,
  AllocationReferenceNumber = a.qpaAllocationRef,
  AllocationDate = a.qpaAllocationDate,
  AllocatedCapitalAmount = ad.qpadCapitalAmountCLOSED,
  AllocatedInterestAmount = ad.qpadInterestAmountCLOSED,
  AllocatedBSMVAmount = ad.qpadBSMVCLOSED,
  AllocatedKKDFAmount = ad.qpadKKDFCLOSED
FROM qtPaymentAllocDetail ad (NOLOCK)
JOIN qtPaymentAlloc a (NOLOCK) ON a.BaseID = ad.MasterID;
```

### 15. DataStructure-Aligned Reescompte (Non-Accrual Current Interest)
```sql
DECLARE @dReportDate date = GETDATE();
SELECT
  CreditID = p.ppQuotationID,
  ReescompteAmount =
    CASE
      WHEN p.ppActivationDate > ISNULL(pd.AssumedDueDatePast, pd.AssumedDueDate) THEN 0
      WHEN pd.ppInterestAmount = 0 OR pd.DayTot = 0 OR pd.DayPast = 0 OR pd.InstallmentIsCLOSED_FL = 1 THEN 0
      ELSE pd.ppInterestAmount / pd.DayTot * CASE WHEN pd.DayPast > pd.DayTot THEN pd.DayTot ELSE pd.DayPast END
    END
FROM qtPaymentPlan p
OUTER APPLY (
  SELECT TOP 1
    DayTot = DATEDIFF(DD, ISNULL(pdx.ppAssumedDueDate, p.ppActivationDate), pd.ppAssumedDueDate),
    DayPast = DATEDIFF(DD, ISNULL(pdx.ppAssumedDueDate, p.ppActivationDate), @dReportDate),
    ppInterestAmount = pd.ppInterestAmount,
    AssumedDueDate = pd.ppAssumedDueDate,
    AssumedDueDatePast = pdx.ppAssumedDueDate,
    InstallmentIsCLOSED_FL = ISNULL(pd.ppInstallmentIsCLOSED_FL, 0)
  FROM qtPaymentPlanDetail pd (NOLOCK)
  OUTER APPLY (
    SELECT TOP 1 pdx.ppAssumedDueDate
    FROM qtPaymentPlanDetail pdx (NOLOCK)
    WHERE pdx.pPaymentPlanID = pd.pPaymentPlanID
      AND pd.ppPaymentAmountRowIndex - 1 = pdx.ppPaymentAmountRowIndex
      AND pd.pPaymentPlanType = pdx.pPaymentPlanType
  ) pdx
  WHERE pd.pPaymentPlanType = 0
    AND pd.ppAssumedDueDate > @dReportDate
    AND p.BaseID = pd.pPaymentPlanID
  ORDER BY pd.ppAssumedDueDate ASC
) pd
WHERE p.ppQuotationID IS NOT NULL
  AND NOT (pd.ppInterestAmount = 0 OR pd.DayTot = 0 OR pd.DayPast = 0 OR pd.InstallmentIsCLOSED_FL = 1);
```

### 16. DataStructure-Aligned Accounting Menu Definitions
```sql
SELECT
  AccountingMenuID = GLTypeID,    -- maps to PaymentVoucherDetail.GLType
  AccountingMenuCode = GLTypeCode,
  AccountingMenuDescription = dbo.getDataFromLangId(GLDescription_mlg, 2)
FROM AccountGeneralLedgerTypes
WHERE GLTypeID IN (1,3,4,5,8,11,12,13,14,16,19,21,22,23,24,25,27,28,29,31,32,33,34,35,36,37,53);
```

| Object | Type | Returns / Does |
|---|---|---|
| `dbo.getWorkingDate_tbl_inline(date)` | TVF | Next working date in `MinTarih` column |
| `dbo.getCalendarWorkingDate(date)` | TVF | Calendar-aware working day in `VALUE` column (use when DPD < 20 days) |
| `dbo.get_LateDayCountWithBalance(quotationID, date)` | TVF | `LateDayCount`, `LateBalance`, `AssumedDueDateMin` |
| `dbo.GETAllocSUMofDetailsbyPPDID(ppdID, date)` | TVF | `DTotalClosedORG`, `ppdDTotalDiscountClosed` per installment |
| `dbo.getCreditGroupCode(quotationID, date)` | TVF | `CreditGroupCode` for classification |
| `dbo.checkIsInstallmentLate(ppdID, null, dueDate, isClosed)` | Scalar | `1` if installment is late |
| `dbo.getDataFromLangId(mlg_field, langID)` | Scalar | Multilingual name (`langID=2` = Turkish) |
| `dbo.GetCreditAccountTerm(quotationID)` | Scalar | Credit account term |
| `dbo.getparite(date, fromCurrency, toCurrency, quotationID, side)` | Scalar | FX exchange rate |
| `sp_AccountingGLP2Internal @pSQuoteID` | SP | Posts accrual GL for a single credit |
| `sp_LateInterestCalculation @pSQuotationID` | SP | Recalculates late interest for a credit |
| `sp_EndOfDayInterestCalculation @pTranType, @pQuotationId` | SP | EOD interest calc for rotating credits |

---

## Performance & Engineering Pitfalls

Lessons captured from production query work — these are the *known traps* in
this database environment.  Every CreditReport / Reconciliation style query
should respect them or it will silently produce wrong numbers, time out, or
both.

### 1. `Customer.cClientIdentityID` is masked — always LEFT JOIN

The customer national-ID column is masked at column level via `DataMask` while
`Quotation.qtClientNationalID` retains the real value (or vice-versa).  The
two will **never** match for any row.

| Pattern | Result |
|---|---|
| `JOIN Customer c ON c.cClientIdentityID = q.qtClientNationalID` | **0 rows** — silent data loss |
| `LEFT JOIN Customer c ON c.cClientIdentityID = q.qtClientNationalID` | Correct row count, customer columns NULL |

If a non-nullable customer attribute is required, source it from `Person`
linkage or `qtClientName` instead of relying on the identity-number match.

### 2. `H_GL` is a HEAP — pre-materialise, do not join directly at scale

`dbo.h_gl` has no clustered index and no index on `TRN_MENU`.  Direct CTE
joins force RID Lookups per outer row, causing:

- Quadratic scaling above ~275 rows (NL + RID Lookup explosion)
- Plan flips when memory grant is exceeded (hash spill to tempdb)

**Pattern:** materialise the relevant slice into a `#temp` with a clustered
index *before* the main query references it.

```sql
SELECT GANALIZKOD1, TRN_TARIHI, GTUTARIT, GOZELKOD1
INTO #HGL_Slice
FROM dbo.h_gl WITH (NOLOCK)
WHERE TRN_MENU   = 'TGLP1'   -- or the menu list you need
  AND GTIPI      = 'B'
  AND TRN_TARIHI <= @ReportDate
  AND EXISTS (SELECT 1 FROM #CreditScope cs WHERE cs.ContractCode = H_GL.GANALIZKOD1);

CREATE CLUSTERED INDEX IX_HGLSlice ON #HGL_Slice (GANALIZKOD1, TRN_TARIHI);
```

Apply the same materialisation pattern to `#CreditScope` and `#ActivePlan` —
chained CTEs are otherwise inline-expanded N times by the optimiser.

### 3. Global `OPTION (HASH JOIN, MERGE JOIN)` — Msg 8622 risk

Global join hints in the query OPTION clause apply to **every** join.  Some
joins (single-row lookups, certain APPLYs) cannot be expressed as hash/merge,
which makes the plan tree invalid:

```
Msg 8622, Level 16, State 1
Query processor could not produce a query plan because of the hints defined
in this query.
```

**Do not use** `OPTION (HASH JOIN, MERGE JOIN)` to fight a plan flip.  Instead:

- Materialise the unstable input into a `#temp` (Section 2)
- Use **per-join hints** if needed: `INNER HASH JOIN`, `INNER MERGE JOIN`
- Keep `OPTION (RECOMPILE, MAXDOP n)` for parameter-sensitivity stabilisation

### 4. Scalar UDFs serialise the plan — inline when possible

`dbo.calculateReescont` (and any other deterministic scalar UDF) suppresses
parallel plans on this build.  Inline the body when it is simple arithmetic.

`calculateReescont(@startDate, @endDate, @amount, @currentDate)` reduces to:

```sql
DECLARE @TotalDays  int = DATEDIFF(DAY, @startDate, @endDate);
DECLARE @CurrentDays int = CASE WHEN DATEDIFF(DAY, @startDate, @currentDate) + 1 < 0
                                THEN 0
                                ELSE DATEDIFF(DAY, @startDate, @currentDate) + 1
                           END;

ROUND(
    CASE
        WHEN @CurrentDays = @TotalDays              THEN @amount
        WHEN @TotalDays * @CurrentDays = 0          THEN 0
        ELSE @amount / @TotalDays * @CurrentDays
    END, 2)
```

When the same `(StartDate, EndDate, CurrentDate)` triple feeds multiple amount
calls, hoist `@TotalDays`/`@CurrentDays` into a single `CROSS APPLY` so the
day arithmetic runs once per row, not per amount.

### 5. `getWorkingDate()` applies only to the FIRST late installment

When a payment plan has more than one overdue installment, the working-date
adjustment (`dbo.getWorkingDate(ppAssumedDueDate)`) must be applied **only to
the first** late installment.  Subsequent late installments retain the
original `ppAssumedDueDate`.

Implementation pattern (see `GLP2_LateInstallment_Correction.sql`):

```sql
;WITH LatePPD AS (
    SELECT ppd.*,
           LateOrder = DENSE_RANK() OVER (
                          PARTITION BY ppd.pPaymentPlanID
                          ORDER BY     ppd.ppPaymentAmountRowIndex ASC)
    FROM qtPaymentPlanDetail ppd
    WHERE ...
)
SELECT ...
       AssumedDueDate = CASE WHEN LateOrder = 1
                             THEN dbo.getWorkingDate(ppAssumedDueDate)
                             ELSE ppAssumedDueDate
                        END
FROM LatePPD;
```

Use `DENSE_RANK` (not `ROW_NUMBER`) so multiple `pPaymentPlanType` rows
sharing the same installment number share the same `LateOrder` value.

### 6. NULL discipline in subtractions and aggregates

Mask-protected columns and partial closures regularly produce NULL operands.
A single NULL anywhere in an arithmetic chain corrupts the whole expression,
and `SUM(CASE WHEN ... THEN col END)` returns NULL when *every* matching row
is NULL.

**Rules:**

| Operation | Required wrapping |
|---|---|
| Subtraction `a - b` | `ISNULL(a, 0) - ISNULL(b, 0)` |
| Multiplicative chain `a * b / c * d` | Wrap every factor in `ISNULL(., 0)` |
| `SUM(CASE ... THEN col END)` | `SUM(CASE ... THEN ISNULL(col, 0) ELSE 0 END)` |
| `MIN`/`MAX` of a date | Leave bare — NULL means "no row matched", which is the correct semantics |
| `MIN`/`MAX` of an amount | Wrap with `ISNULL` if zero is the intended fallback |

For Excel exports, NULL renders as empty cell — pivot tables and downstream
sums break.  Always coalesce numeric outputs at the final SELECT.

### 7. Plan stability: `#temp` over CTE for reused result sets

A CTE is *not* a materialised view — the optimiser inlines it at every
reference site.  When a result set (e.g. `#CreditScope`) is referenced 4-5
times in the final query, expect 4-5x scan cost.

Migrate to `SELECT INTO #temp` plus indexes when:

- The CTE is referenced more than twice in the same statement
- The CTE result is small (< 10K rows) and the outer set is large
- A scalar UDF or filtered predicate makes inlining especially costly

The `#CreditScope` / `#ActivePlan` / `#HGL_Slice` pattern in
`CreditReport.sql` exists precisely for this reason.

---

## Data Quality Control Checklist

For recurring reconciliation runs, check the following:

| Issue | Detection Logic |
|---|---|
| Closed credit with outstanding principal | `csCreditCode='END'` AND `SUM(ppCapitalAmount - ppCapitalAmountCLOSED) <> 0` |
| Missing accrual GL entry | Installment due, `ppInterestAmount > 0`, no `PaymentVoucherDetail` with `GLType=8` |
| Allocation cancelled but GL not reversed | `qtPaymentAllocCancellation` exists, no corresponding `PaymentVoucherDetail` reversal |
| Duplicate accrual cancellations | Multiple `qtPaymentAllocCancellation` rows for same installment |
| Late payment with wrong GL flag | `SPSource LIKE '%gecodeme=0%'` but `ppInstallmentClosedDate > ppAssumedDueDate` |
| Installment total vs component sum mismatch | `ppTotalPayment <> ppCapitalAmount + ppInterestAmount + ppBMSV + ppKKDF` |
| Receipt unallocated balance | `qprReceiptAmount - qprReceiptAmountClosed > 0` after expected allocation window |
| Cut credit DPD inconsistency | `ppCutDPD` does not match `DATEDIFF(DAY, ppCutMinAssumedDueDate, ppCutDate)` |
| Missing late accrual for overdue rows | `ppAssumedDueDate < GETDATE()` AND `ppAccuralDate IS NULL` AND `pPaymentPlanType = -5` |

**Audit log fallback:** `SETBaseVizyonlog..qtPaymentPlanDetail` — historical plan rows; use `TranType = '666'` for cut-related log entries.

---

## Skill Checklist for Future Reports

Given the above, I can produce without referencing source SQL files:

- Full installment schedule with all financial components (`TODAY`, `CLOSED`, `DISCOUNT` variants)
- Open/closed portfolio classification and counts (`VA`,`LI`,`GD`,`END`)
- Payment-type-aware breakdown (`0`=aggregate, `-1`=single product, `-4`=accrual interest, `-5`=late interest, `-6`=fees, `-7`=accrual FX diff, `-8`=payment FX diff, `-10`=commission)
- Exact remaining balance using the full discount formula
- Credit-level and customer-level overdue portfolio (DPD + balance)
- Portfolio trend by year/quarter and customer type
- Receipt-to-installment allocation ledger including cancellations
- Advance (unallocated) balance per receipt
- Reescompte / accrued interest reports by any reference date (including cut/cancel/rotating edge cases)
- Rotating credit (`ppProductTypeID=19`) daily accrual reconciliation
- Follow-up/cut credit analytics (`ppCut*` fields, post-cut installment rows)
- GL missing-entry detection (accrual gltype=8, collection gltype=12)
- GL vs operational balance cross-check
- Collateral / security reports (vehicle pledge, guarantors, additional collateral, EGM events)
- Credit IRR / effective interest from `qtPaymentPlanCalcDetails`
- Vehicle / product enrichment reports (brand, model, VIN, side products)
- Delinquency aging bucket classification
- Month-end reconciliation control packs
