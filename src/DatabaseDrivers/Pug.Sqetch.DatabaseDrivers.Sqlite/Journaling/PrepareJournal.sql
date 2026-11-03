create table if not exists sqetch_journal (
	id integer primary key autoincrement,
	project text not null,
	slot text not null,
	release text not null,
	plan text,
	step text,
	description text,
	at_utc text not null
)
;;
create index if not exists sqetch_journal_release on sqetch_journal ( project, release )
