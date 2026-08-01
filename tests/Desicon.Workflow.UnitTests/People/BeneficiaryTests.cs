using Desicon.Workflow.Domain.People;
using FluentAssertions;
using Xunit;

namespace Desicon.Workflow.UnitTests.People;

/// <summary>
/// HasBankDetails is what RequestActionService.RunTransitionAsync stages
/// into ExpenseRequest.BeneficiaryHasBankDetails for the AUTHORISE guard --
/// see GuardEvaluatorTests.Authorise_guard_allows_bank_transfer_only_with_bank_details.
/// </summary>
public sealed class BeneficiaryTests
{
    [Theory]
    [InlineData("First Bank", "0123456789", true)]
    [InlineData("", "0123456789", false)]
    [InlineData("First Bank", "", false)]
    [InlineData("", "", false)]
    [InlineData(null, null, false)]
    public void HasBankDetails_requires_both_bank_name_and_account_number(
        string? bankName, string? bankAccountNumber, bool expected)
    {
        var beneficiary = new Beneficiary
        {
            BankName = bankName!,
            BankAccountNumber = bankAccountNumber!
        };

        beneficiary.HasBankDetails.Should().Be(expected);
    }

    [Fact]
    public void Whitespace_only_bank_details_do_not_count_as_set()
    {
        var beneficiary = new Beneficiary { BankName = "   ", BankAccountNumber = "0123456789" };

        beneficiary.HasBankDetails.Should().BeFalse();
    }
}
