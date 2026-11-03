select distinct p.plan from sqetch_journal p
where p.project = @project and p.release = @release and p.slot = 'PlanDeployed'
	and not exists ( select 1 from sqetch_journal r
		where r.project = @project and r.release = p.release and r.plan = p.plan
			and r.slot = 'RolledBackPlan' and r.id > p.id )
