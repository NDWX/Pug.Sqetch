select plan from sqetch_journal
where project = @project and release = @release and slot = 'PlanDeployed'
