namespace Sankore.Modules.Workflow.Domain;

/// <summary>
/// Nature métier d'une étape, telle que le constructeur de parcours la présente.
///
/// Distincte de <see cref="StateType"/>, qui décrit le rôle du nœud dans la machine à états :
/// le moteur exécute chaque étape comme une validation ordonnée, SAUF celles que
/// <see cref="StepKinds.RunsAutomatically"/> désigne (la notification), franchies dès qu'on y entre.
/// Le type et sa configuration (<see cref="WorkflowStepDefinition.ConfigJson"/>) sont enregistrés
/// pour que le parcours décrit soit celui qui est publié ; l'exécution propre des autres types
/// (formulaire, contrôle KYC…) relève des évolutions du moteur.
/// </summary>
public enum StepKind
{
    Form,
    QualificationForm,
    KycCheck,
    Validation,
    ManualTask,
    Condition,
    Wait,
    Notification,
    IntegrationAction
}

public static class StepKinds
{
    /// <summary>
    /// A step nobody decides: the engine runs it on entry and moves on. Only the notification so far —
    /// it used to wait for an approval nobody was asked for (no role), and the instance stopped there.
    /// </summary>
    public static bool RunsAutomatically(StepKind kind) => kind == StepKind.Notification;
}
