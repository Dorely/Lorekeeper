namespace Lorekeeper.Tokens;

public interface ITokenBudgetPlanner
{
    TokenBudgetPlan Plan(TokenBudgetRequest? request = null);
}