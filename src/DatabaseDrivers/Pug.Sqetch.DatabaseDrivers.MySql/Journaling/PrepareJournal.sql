create table if not exists sqetch_journal (
	id bigint auto_increment primary key,
	project varchar(128) not null,
	slot varchar(32) not null,
	`release` varchar(128) not null,
	plan varchar(128),
	step varchar(128),
	description text,
	at_utc datetime(6) not null,
	index sqetch_journal_release ( project, `release` )
)
