namespace Sankore.Modules.Workflow.Domain;

/// <summary>
/// Nature métier d'une étape, telle que le constructeur de parcours la présente.
///
/// Distincte de <see cref="StateType"/>, qui décrit le rôle du nœud dans la machine à états :
/// le moteur exécute aujourd'hui chaque étape comme une validation ordonnée, quel que soit son
/// <c>Kind</c>. Le type et sa configuration (<see cref="WorkflowStepDefinition.ConfigJson"/>) sont
/// enregistrés pour que le parcours décrit soit celui qui est publié ; leur exécution spécifique
/// (formulaire, contrôle KYC, notification…) relève des évolutions du moteur.
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
