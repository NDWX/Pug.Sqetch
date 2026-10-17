insert into sqetch_journal ( project, slot, release, plan, step, description, at_utc ) values ( @project, 'StepDeployed', @release, @plan, @step, @description, @utcTimestamp )
